package outboundgroup

import (
	"fmt"
	"net"
	"math"
	"sync"
	"sync/atomic"
	"time"

	"github.com/metacubex/mihomo/common/buf"
	C "github.com/metacubex/mihomo/constant"
)

const (
	weightedBytesWindowSeconds = 30
	weightedBytesWarmup        = 10 * time.Second
	virtualFlowDebt            = uint64(64 * 1024)
	maximumFlowDebt            = uint64(1024 * 1024)
)

type byteWindowBucket struct {
	second int64
	bytes  atomic.Uint64
}

type byteWindow struct {
	slots [weightedBytesWindowSeconds]atomic.Pointer[byteWindowBucket]
}

func (window *byteWindow) recordAt(now time.Time, count uint64) {
	if count == 0 { return }
	second := now.Unix()
	index := int((second%weightedBytesWindowSeconds + weightedBytesWindowSeconds) % weightedBytesWindowSeconds)
	slot := &window.slots[index]
	for {
		bucket := slot.Load()
		if bucket != nil && bucket.second == second { bucket.bytes.Add(count); return }
		// A delayed callback for an old flow must not replace a newer bucket in this ring slot.
		if bucket != nil && bucket.second > second { return }
		next := &byteWindowBucket{second: second}
		if slot.CompareAndSwap(bucket, next) { next.bytes.Add(count); return }
	}
}

func (window *byteWindow) sumAt(now time.Time, windowSeconds int64) uint64 {
	current := now.Unix()
	var total uint64
	for i := range window.slots {
		bucket := window.slots[i].Load()
		if bucket == nil || bucket.second > current || bucket.second <= current-windowSeconds { continue }
		total += bucket.bytes.Load()
	}
	return total
}

type proxyByteCounter struct {
	upload        byteWindow
	download      byteWindow
	totalUpload   atomic.Uint64
	totalDownload atomic.Uint64
	debt          atomic.Uint64
}

func (counter *proxyByteCounter) recordAt(now time.Time, upload, download uint64) {
	count := upload + download
	if count == 0 { return }
	counter.upload.recordAt(now, upload)
	counter.download.recordAt(now, download)
	counter.totalUpload.Add(upload)
	counter.totalDownload.Add(download)
	counter.reduceDebt(count)
}

func (counter *proxyByteCounter) windowBytes(now time.Time, windowSeconds int64) (upload, download uint64) {
	return counter.upload.sumAt(now, windowSeconds), counter.download.sumAt(now, windowSeconds)
}

func (counter *proxyByteCounter) reduceDebt(count uint64) {
	for {
		old := counter.debt.Load()
		if old == 0 { return }
		next := uint64(0)
		if count < old { next = old - count }
		if counter.debt.CompareAndSwap(old, next) { return }
	}
}

type weightedCandidate struct {
	proxy  C.Proxy
	name   string
	weight int
	bytes  uint64
	debt   uint64
}

type weightedBytesStrategy struct {
	mu           sync.Mutex
	weights      map[string]int
	virtual      map[string]int64
	warmStart    map[string]time.Time
	wasUnhealthy map[string]bool
	counters     sync.Map // map[string]*proxyByteCounter; active flow wrappers retain their counter pointer.
	windowSecond int64
	now          func() time.Time
}

func newWeightedBytesStrategy(weights map[string]int, now func() time.Time) *weightedBytesStrategy {
	return newWeightedBytesStrategyWithWindow(weights, weightedBytesWindowSeconds, now)
}

func newWeightedBytesStrategyWithWindow(weights map[string]int, windowSeconds int, now func() time.Time) *weightedBytesStrategy {
	if now == nil { now = time.Now }
	strategy := &weightedBytesStrategy{
		weights: make(map[string]int, len(weights)), virtual: make(map[string]int64),
		warmStart: make(map[string]time.Time), wasUnhealthy: make(map[string]bool),
		windowSecond: int64(windowSeconds), now: now,
	}
	for name, weight := range weights { strategy.weights[name] = weight; strategy.counterFor(name) }
	return strategy
}

func (strategy *weightedBytesStrategy) strategy(testURL string) strategyFn {
	return func(proxies []C.Proxy, _ *C.Metadata, touch bool) C.Proxy { return strategy.selectProxy(proxies, testURL, touch) }
}

func (strategy *weightedBytesStrategy) selectProxy(proxies []C.Proxy, testURL string, touch bool) C.Proxy {
	if len(proxies) == 0 { return nil }
	now := strategy.now()
	strategy.mu.Lock()
	defer strategy.mu.Unlock()

	eligible := make([]weightedCandidate, 0, len(proxies))
	weightSum := 0
	for _, proxy := range proxies {
		name := proxy.Name()
		if !proxy.AliveForTestUrl(testURL) {
			strategy.wasUnhealthy[name] = true
			delete(strategy.warmStart, name)
			continue
		}
		if strategy.wasUnhealthy[name] {
			strategy.warmStart[name] = now
			delete(strategy.wasUnhealthy, name)
		}
		weight, exists := strategy.weights[name]
		if !exists { weight = 1 }
		if weight <= 0 { continue }
		if start, warming := strategy.warmStart[name]; warming {
			elapsed := now.Sub(start)
			if elapsed < weightedBytesWarmup {
				scaled := weight * int(maxInt64(0, elapsed.Milliseconds())) / int(weightedBytesWarmup.Milliseconds())
				weight = maxInt(1, scaled)
			} else { delete(strategy.warmStart, name) }
		}
		counter := strategy.counterFor(name)
		up, down := counter.windowBytes(now, strategy.windowSecond)
		eligible = append(eligible, weightedCandidate{proxy: proxy, name: name, weight: weight, bytes: up + down, debt: counter.debt.Load()})
		weightSum += weight
	}
	if len(eligible) == 0 { return proxies[0] }
	if weightSum == 0 { return proxies[0] }

	var totalBytes, totalDebt uint64
	for _, candidate := range eligible { totalBytes += candidate.bytes; totalDebt += candidate.debt }
	selected := 0
	if totalBytes == 0 && totalDebt == 0 {
		selected = strategy.smoothWeightedSelection(eligible, weightSum, touch)
	} else {
		best := math.Inf(-1)
		for index, candidate := range eligible {
			observed := candidate.bytes + candidate.debt/8
			total := totalBytes + totalDebt/8
			deficit := float64(total)*float64(candidate.weight)/float64(weightSum) - float64(observed)
			if deficit > best { best = deficit; selected = index }
		}
	}

	if touch {
		counter := strategy.counterFor(eligible[selected].name)
		for {
			old := counter.debt.Load()
			next := minUint(maximumFlowDebt, old+virtualFlowDebt)
			if counter.debt.CompareAndSwap(old, next) { break }
		}
	}
	return eligible[selected].proxy
}

func (strategy *weightedBytesStrategy) smoothWeightedSelection(candidates []weightedCandidate, total int, touch bool) int {
	selected := 0
	best := int64(-1 << 63)
	for index, candidate := range candidates {
		current := strategy.virtual[candidate.name] + int64(candidate.weight)
		if current > best { selected = index; best = current }
		if touch { strategy.virtual[candidate.name] = current }
	}
	if touch { strategy.virtual[candidates[selected].name] -= int64(total) }
	return selected
}

func (strategy *weightedBytesStrategy) counterFor(proxyName string) *proxyByteCounter {
	if counter, ok := strategy.counters.Load(proxyName); ok { return counter.(*proxyByteCounter) }
	counter := &proxyByteCounter{}
	actual, _ := strategy.counters.LoadOrStore(proxyName, counter)
	return actual.(*proxyByteCounter)
}

func (strategy *weightedBytesStrategy) recordBytes(proxyName string, upload, download uint64, at time.Time) {
	strategy.counterFor(proxyName).recordAt(at, upload, download)
}

func (strategy *weightedBytesStrategy) windowBytes(proxyName string, at time.Time) (upload, download uint64) {
	return strategy.counterFor(proxyName).windowBytes(at, strategy.windowSecond)
}

func (strategy *weightedBytesStrategy) setWeights(weights map[string]int) error {
	if err := validateWeightValues(weights); err != nil { return err }
	strategy.mu.Lock()
	defer strategy.mu.Unlock()
	strategy.weights = make(map[string]int, len(weights))
	for name, weight := range weights { strategy.weights[name] = weight; strategy.counterFor(name) }
	return nil
}

func validateWeightValues(weights map[string]int) error {
	if len(weights) == 0 { return fmt.Errorf("at least one proxy weight is required") }
	total := 0
	for name, weight := range weights {
		if name == "" || weight <= 0 { return fmt.Errorf("target weight for %q must be positive", name) }
		total += weight
	}
	if total <= 0 { return fmt.Errorf("at least one proxy must have a positive target weight") }
	return nil
}

func (strategy *weightedBytesStrategy) weightSnapshot() map[string]int {
	strategy.mu.Lock()
	defer strategy.mu.Unlock()
	result := make(map[string]int, len(strategy.weights))
	for name, weight := range strategy.weights { result[name] = weight }
	return result
}

func (strategy *weightedBytesStrategy) stats(proxies []C.Proxy, testURL string, now time.Time) map[string]any {
	strategy.mu.Lock()
	defer strategy.mu.Unlock()
	weights := make(map[string]int, len(proxies))
	totalWeight := 0
	for _, proxy := range proxies {
		weight, exists := strategy.weights[proxy.Name()]
		if !exists { weight = 1 }
		weights[proxy.Name()] = weight
		totalWeight += weight
	}
	rolling := make(map[string][2]uint64, len(proxies))
	var allRolling uint64
	for _, proxy := range proxies {
		up, down := strategy.counterFor(proxy.Name()).windowBytes(now, strategy.windowSecond)
		rolling[proxy.Name()] = [2]uint64{up, down}
		allRolling += up + down
	}
	result := make(map[string]any, len(proxies))
	for _, proxy := range proxies {
		name := proxy.Name()
		counter := strategy.counterFor(name)
		weight := weights[name]
			effective := float64(weight)
		progress := 1.0
		if started, warming := strategy.warmStart[name]; warming {
			progress = clampFloat(float64(now.Sub(started))/float64(weightedBytesWarmup), 0, 1)
			effective = maxFloat(1, float64(weight)*progress)
		}
		up, down := rolling[name][0], rolling[name][1]
		actualShare := 0.0
		if allRolling > 0 { actualShare = float64(up+down) * 100 / float64(allRolling) }
		targetShare := 0.0
		if totalWeight > 0 { targetShare = float64(weight) * 100 / float64(totalWeight) }
		result[name] = map[string]any{
			"configuredWeight": weight, "effectiveWeight": effective, "targetShare": targetShare,
			"actualShare": actualShare, "rollingBytes": up + down,
			"uploadBytes": counter.totalUpload.Load(), "downloadBytes": counter.totalDownload.Load(),
			"rollingUploadBytes": up, "rollingDownloadBytes": down,
			"healthy": proxy.AliveForTestUrl(testURL), "warmingUp": progress < 1.0, "warmupProgress": progress,
		}
	}
	return result
}

type weightedBytesConn struct { C.Conn; counter *proxyByteCounter }

func (conn *weightedBytesConn) Read(bytes []byte) (int, error) {
	n, err := conn.Conn.Read(bytes); conn.counter.recordAt(time.Now(), 0, uint64(maxInt(0, n))); return n, err
}
func (conn *weightedBytesConn) ReadBuffer(buffer *buf.Buffer) error {
	err := conn.Conn.ReadBuffer(buffer); conn.counter.recordAt(time.Now(), 0, uint64(maxInt(0, buffer.Len()))); return err
}
func (conn *weightedBytesConn) Write(bytes []byte) (int, error) {
	n, err := conn.Conn.Write(bytes); conn.counter.recordAt(time.Now(), uint64(maxInt(0, n)), 0); return n, err
}
func (conn *weightedBytesConn) WriteBuffer(buffer *buf.Buffer) error {
	count := buffer.Len(); err := conn.Conn.WriteBuffer(buffer); conn.counter.recordAt(time.Now(), uint64(maxInt(0, count)), 0); return err
}

type weightedBytesPacketConn struct { C.PacketConn; counter *proxyByteCounter }

func (conn *weightedBytesPacketConn) ReadFrom(bytes []byte) (int, net.Addr, error) {
	n, addr, err := conn.PacketConn.ReadFrom(bytes); conn.counter.recordAt(time.Now(), 0, uint64(maxInt(0, n))); return n, addr, err
}
func (conn *weightedBytesPacketConn) WaitReadFrom() ([]byte, func(), net.Addr, error) {
	data, put, addr, err := conn.PacketConn.WaitReadFrom(); conn.counter.recordAt(time.Now(), 0, uint64(len(data))); return data, put, addr, err
}
func (conn *weightedBytesPacketConn) WriteTo(bytes []byte, addr net.Addr) (int, error) {
	n, err := conn.PacketConn.WriteTo(bytes, addr); conn.counter.recordAt(time.Now(), uint64(maxInt(0, n)), 0); return n, err
}

func maxInt(left, right int) int { if left > right { return left }; return right }
func maxInt64(left, right int64) int64 { if left > right { return left }; return right }
func minUint(left, right uint64) uint64 { if left < right { return left }; return right }
func maxFloat(left, right float64) float64 { if left > right { return left }; return right }
func clampFloat(value, low, high float64) float64 { if value < low { return low }; if value > high { return high }; return value }
