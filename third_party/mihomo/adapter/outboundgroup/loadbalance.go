package outboundgroup

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"sync"
	"time"

	"github.com/metacubex/mihomo/common/callback"
	"github.com/metacubex/mihomo/common/lru"
	N "github.com/metacubex/mihomo/common/net"
	"github.com/metacubex/mihomo/common/utils"
	C "github.com/metacubex/mihomo/constant"
	P "github.com/metacubex/mihomo/constant/provider"

	"golang.org/x/net/publicsuffix"
)

type LoadBalanceOption struct {
	Strategy string `group:"strategy,omitempty"`
	Weights  map[string]int `group:"weights,omitempty"`
	ByteWindow string `group:"byte-window,omitempty"`
}

type LoadBalance struct {
	*GroupBase
	disableUDP     bool
	strategyFn     strategyFn
	byteStrategy   *weightedBytesStrategy
	testUrl        string
	expectedStatus string
}

type strategyFn = func(proxies []C.Proxy, metadata *C.Metadata, touch bool) C.Proxy

var errStrategy = errors.New("unsupported strategy")

func getKey(metadata *C.Metadata) string {
	if metadata == nil {
		return ""
	}

	if metadata.Host != "" {
		// ip host
		if ip := net.ParseIP(metadata.Host); ip != nil {
			return metadata.Host
		}

		if etld, err := publicsuffix.EffectiveTLDPlusOne(metadata.Host); err == nil {
			return etld
		}
	}

	if !metadata.DstIP.IsValid() {
		return ""
	}

	return metadata.DstIP.String()
}

func getKeyWithSrcAndDst(metadata *C.Metadata) string {
	dst := getKey(metadata)
	src := ""
	if metadata != nil {
		src = metadata.SrcIP.String()
	}

	return fmt.Sprintf("%s%s", src, dst)
}

func jumpHash(key uint64, buckets int32) int32 {
	var b, j int64

	for j < int64(buckets) {
		b = j
		key = key*2862933555777941757 + 1
		j = int64(float64(b+1) * (float64(int64(1)<<31) / float64((key>>33)+1)))
	}

	return int32(b)
}

// DialContext implements C.ProxyAdapter
func (lb *LoadBalance) DialContext(ctx context.Context, metadata *C.Metadata) (c C.Conn, err error) {
	proxy := lb.Unwrap(metadata, true)
	c, err = proxy.DialContext(ctx, metadata)

	if err == nil {
		if lb.byteStrategy != nil {
			c = &weightedBytesConn{Conn: c, counter: lb.byteStrategy.counterFor(proxy.Name())}
		}
		c.AppendToChains(lb)
	} else {
		lb.onDialFailed(proxy.Type(), err, lb.healthCheck)
	}

	if N.NeedHandshake(c) {
		c = callback.NewFirstWriteCallBackConn(c, func(err error) {
			if err == nil {
				lb.onDialSuccess()
			} else {
				lb.onDialFailed(proxy.Type(), err, lb.healthCheck)
			}
		})
	}

	return
}

// ListenPacketContext implements C.ProxyAdapter
func (lb *LoadBalance) ListenPacketContext(ctx context.Context, metadata *C.Metadata) (pc C.PacketConn, err error) {
	proxy := lb.Unwrap(metadata, true)
	pc, err = proxy.ListenPacketContext(ctx, metadata)
	if err != nil { return nil, err }
	if lb.byteStrategy != nil { pc = &weightedBytesPacketConn{PacketConn: pc, counter: lb.byteStrategy.counterFor(proxy.Name())} }
	pc.AppendToChains(lb)
	return pc, nil
}

// SupportUDP implements C.ProxyAdapter
func (lb *LoadBalance) SupportUDP() bool {
	return !lb.disableUDP
}

// IsL3Protocol implements C.ProxyAdapter
func (lb *LoadBalance) IsL3Protocol(metadata *C.Metadata) bool {
	return lb.Unwrap(metadata, false).IsL3Protocol(metadata)
}

func strategyRoundRobin(url string) strategyFn {
	idx := 0
	idxMutex := sync.Mutex{}
	return func(proxies []C.Proxy, metadata *C.Metadata, touch bool) C.Proxy {
		idxMutex.Lock()
		defer idxMutex.Unlock()

		i := 0
		length := len(proxies)

		if touch {
			defer func() {
				idx = (idx + i) % length
			}()
		}

		for ; i < length; i++ {
			id := (idx + i) % length
			proxy := proxies[id]
			if proxy.AliveForTestUrl(url) {
				i++
				return proxy
			}
		}

		return proxies[0]
	}
}

func strategyConsistentHashing(url string) strategyFn {
	maxRetry := 5
	return func(proxies []C.Proxy, metadata *C.Metadata, touch bool) C.Proxy {
		key := utils.MapHash(getKey(metadata))
		buckets := int32(len(proxies))
		for i := 0; i < maxRetry; i, key = i+1, key+1 {
			idx := jumpHash(key, buckets)
			proxy := proxies[idx]
			if proxy.AliveForTestUrl(url) {
				return proxy
			}
		}

		// when availability is poor, traverse the entire list to get the available nodes
		for _, proxy := range proxies {
			if proxy.AliveForTestUrl(url) {
				return proxy
			}
		}

		return proxies[0]
	}
}

func strategyStickySessions(url string) strategyFn {
	ttl := time.Minute * 10
	maxRetry := 5
	lruCache := lru.New[uint64, int](
		lru.WithAge[uint64, int](int64(ttl.Seconds())),
		lru.WithSize[uint64, int](1000))
	return func(proxies []C.Proxy, metadata *C.Metadata, touch bool) C.Proxy {
		key := utils.MapHash(getKeyWithSrcAndDst(metadata))
		length := len(proxies)
		idx, has := lruCache.Get(key)
		if !has || idx >= length {
			idx = int(jumpHash(key+uint64(time.Now().UnixNano()), int32(length)))
		}

		nowIdx := idx
		for i := 1; i < maxRetry; i++ {
			proxy := proxies[nowIdx]
			if proxy.AliveForTestUrl(url) {
				if !has || nowIdx != idx {
					lruCache.Set(key, nowIdx)
				}

				return proxy
			} else {
				nowIdx = int(jumpHash(key+uint64(time.Now().UnixNano()), int32(length)))
			}
		}

		lruCache.Set(key, 0)
		return proxies[0]
	}
}

// Unwrap implements C.ProxyAdapter
func (lb *LoadBalance) Unwrap(metadata *C.Metadata, touch bool) C.Proxy {
	proxies := lb.GetProxies(touch)
	return lb.strategyFn(proxies, metadata, touch)
}

// MarshalJSON implements C.ProxyAdapter
func (lb *LoadBalance) MarshalJSON() ([]byte, error) {
	var all []string
	proxies := lb.GetProxies(false)
	for _, proxy := range proxies {
		all = append(all, proxy.Name())
	}
	result := map[string]any{
		"type":           lb.Type().String(),
		"all":            all,
		"testUrl":        lb.testUrl,
		"expectedStatus": lb.expectedStatus,
		"hidden":         lb.Hidden(),
		"icon":           lb.Icon(),
		"emptyFallback":  lb.EmptyFallback().Name(),
	}
	if lb.byteStrategy != nil {
		result["strategy"] = "weighted-bytes"
		result["weightedBytesSupported"] = true
		result["windowSeconds"] = lb.byteStrategy.windowSecond
		result["weights"] = lb.byteStrategy.weightSnapshot()
		result["byteStats"] = lb.byteStrategy.stats(proxies, lb.testUrl, time.Now())
	}
	return json.Marshal(result)
}

// WeightedBytesSetAble allows the local control API to update target shares without restarting the core.
type WeightedBytesSetAble interface {
	SetWeights(map[string]int) error
}

func (lb *LoadBalance) SetWeights(weights map[string]int) error {
	if lb.byteStrategy == nil {
		return fmt.Errorf("weighted-byte strategy is not enabled")
	}
	known := make(map[string]struct{})
	for _, proxy := range lb.GetProxies(false) { known[proxy.Name()] = struct{}{} }
	for name := range weights { if _, ok := known[name]; !ok { return fmt.Errorf("proxy %q is not a member of load-balance group %q", name, lb.Name()) } }
	return lb.byteStrategy.setWeights(weights)
}

func (lb *LoadBalance) Providers() []P.ProxyProvider {
	return lb.providers
}

func (lb *LoadBalance) Proxies() []C.Proxy {
	return lb.GetProxies(false)
}

func (lb *LoadBalance) Now() string {
	return ""
}

func NewLoadBalance(option GroupCommonOption, loadBalanceOption LoadBalanceOption, emptyFallback C.Proxy, providers []P.ProxyProvider) (lb *LoadBalance, err error) {
	var strategyFn strategyFn
	var byteStrategy *weightedBytesStrategy
	switch loadBalanceOption.Strategy {
	case "", "consistent-hashing":
		strategyFn = strategyConsistentHashing(option.URL)
	case "round-robin":
		strategyFn = strategyRoundRobin(option.URL)
	case "sticky-sessions":
		strategyFn = strategyStickySessions(option.URL)
	case "weighted-bytes":
		window := time.Duration(weightedBytesWindowSeconds) * time.Second
		if loadBalanceOption.ByteWindow != "" {
			window, err = time.ParseDuration(loadBalanceOption.ByteWindow)
			if err != nil { return nil, fmt.Errorf("invalid weighted-byte window: %w", err) }
		}
		if window < 5*time.Second || window > time.Duration(weightedBytesWindowSeconds)*time.Second || window%time.Second != 0 {
			return nil, fmt.Errorf("weighted-byte window must be a whole number of seconds from 5s to %ds", weightedBytesWindowSeconds)
		}
		byteStrategy = newWeightedBytesStrategyWithWindow(loadBalanceOption.Weights, int(window/time.Second), nil)
		strategyFn = byteStrategy.strategy(option.URL)
	default:
		return nil, fmt.Errorf("%w: %s", errStrategy, loadBalanceOption.Strategy)
	}
	lb = &LoadBalance{
		GroupBase: NewGroupBase(GroupBaseOption{
			Name:           option.Name,
			Type:           C.LoadBalance,
			Hidden:         option.Hidden,
			Icon:           option.Icon,
			Filter:         option.Filter,
			ExcludeFilter:  option.ExcludeFilter,
			ExcludeType:    option.ExcludeType,
			TestTimeout:    option.TestTimeout,
			MaxFailedTimes: option.MaxFailedTimes,
			EmptyFallback:  emptyFallback,
			Providers:      providers,
		}),
		strategyFn:     strategyFn,
		byteStrategy:   byteStrategy,
		disableUDP:     option.DisableUDP,
		testUrl:        option.URL,
		expectedStatus: option.ExpectedStatus,
	}
	if byteStrategy != nil {
		proxies := lb.GetProxies(false)
		if len(proxies) == 0 { return nil, fmt.Errorf("weighted-byte group %q has no proxies", option.Name) }
		weights := loadBalanceOption.Weights
		if len(weights) == 0 { weights = make(map[string]int, len(proxies)) }
		known := make(map[string]struct{}, len(proxies))
		for _, proxy := range proxies { known[proxy.Name()] = struct{}{} }
		for name := range weights { if _, ok := known[name]; !ok { return nil, fmt.Errorf("weight names unknown proxy %q in group %q", name, option.Name) } }
		for _, proxy := range proxies { if _, ok := weights[proxy.Name()]; !ok { weights[proxy.Name()] = 1 } }
		if err := validateWeightValues(weights); err != nil { return nil, err }
		if err := byteStrategy.setWeights(weights); err != nil { return nil, err }
	}
	return lb, nil
}
