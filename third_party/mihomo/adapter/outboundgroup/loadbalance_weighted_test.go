package outboundgroup

import (
	"context"
	"fmt"
	"net"
	"sync"
	"testing"
	"time"

	C "github.com/metacubex/mihomo/constant"
)

// selectionTestProxy embeds the interface so a test only needs to implement
// the methods exercised by the selector. Calling any other method is a test
// bug and will panic through the nil embedded interface.
type selectionTestProxy struct {
	C.Proxy
	name  string
	alive bool
	dials int
	packets int
}

func (p *selectionTestProxy) Name() string { return p.name }

func (p *selectionTestProxy) AliveForTestUrl(string) bool { return p.alive }

func (p *selectionTestProxy) DialContext(context.Context, *C.Metadata) (C.Conn, error) {
	p.dials++
	return &selectionTestConn{egress: p.name}, nil
}

func (p *selectionTestProxy) ListenPacketContext(context.Context, *C.Metadata) (C.PacketConn, error) {
	p.packets++
	return &selectionTestPacketConn{egress: p.name}, nil
}

type selectionTestConn struct {
	C.Conn
	egress string
}

func (c *selectionTestConn) AppendToChains(C.ProxyAdapter) {}

func (c *selectionTestConn) RemoteDestination() string { return c.egress }

func (c *selectionTestConn) Write(data []byte) (int, error) { return len(data), nil }

type selectionTestPacketConn struct {
	C.PacketConn
	egress string
}

func (c *selectionTestPacketConn) AppendToChains(C.ProxyAdapter) {}

func (c *selectionTestPacketConn) RemoteDestination() string { return c.egress }

func (c *selectionTestPacketConn) WriteTo(data []byte, net.Addr) (int, error) {
	return len(data), nil
}

var _ C.Proxy = (*selectionTestProxy)(nil)

func selectionTestProxies(names ...string) []C.Proxy {
	proxies := make([]C.Proxy, 0, len(names))
	for _, name := range names {
		proxies = append(proxies, &selectionTestProxy{name: name, alive: true})
	}
	return proxies
}

func TestLegacyLoadBalanceStrategiesRemainAvailable(t *testing.T) {
	proxies := selectionTestProxies("primary", "secondary", "tertiary")
	metadata := &C.Metadata{Host: "stable.example.com"}

	t.Run("round-robin", func(t *testing.T) {
		selectProxy := strategyRoundRobin("")
		for _, want := range []string{"primary", "secondary", "tertiary", "primary"} {
			if got := selectProxy(proxies, metadata, true); got.Name() != want {
				t.Fatalf("selected %q, want %q", got.Name(), want)
			}
		}
	})

	t.Run("consistent-hashing", func(t *testing.T) {
		selectProxy := strategyConsistentHashing("")
		first := selectProxy(proxies, metadata, false)
		if first == nil {
			t.Fatal("selector returned nil")
		}
		for i := 0; i < 20; i++ {
			if got := selectProxy(proxies, metadata, false); got != first {
				t.Fatalf("same destination moved from %q to %q", first.Name(), got.Name())
			}
		}
	})

	t.Run("sticky-sessions", func(t *testing.T) {
		selectProxy := strategyStickySessions("")
		first := selectProxy(proxies, metadata, false)
		if first == nil {
			t.Fatal("selector returned nil")
		}
		for i := 0; i < 20; i++ {
			if got := selectProxy(proxies, metadata, false); got != first {
				t.Fatalf("same session moved from %q to %q", first.Name(), got.Name())
			}
		}
	})
}

func TestLegacyLoadBalanceStrategyNamesRemainAccepted(t *testing.T) {
	fallback := &selectionTestProxy{name: "fallback", alive: true}
	for _, strategy := range []string{"", "consistent-hashing", "round-robin", "sticky-sessions"} {
		t.Run(strategy, func(t *testing.T) {
			if _, err := NewLoadBalance(
				GroupCommonOption{Name: "compatibility"},
				LoadBalanceOption{Strategy: strategy},
				fallback,
				nil,
			); err != nil {
				t.Fatalf("legacy strategy %q was rejected: %v", strategy, err)
			}
		})
	}
}

func BenchmarkLegacyLoadBalanceSelection(b *testing.B) {
	proxies := selectionTestProxies("primary", "secondary", "tertiary")
	metadata := &C.Metadata{Host: "benchmark.example.com"}
	benchmarks := []struct {
		name string
		make func() strategyFn
	}{
		{name: "round-robin", make: func() strategyFn { return strategyRoundRobin("") }},
		{name: "consistent-hashing", make: func() strategyFn { return strategyConsistentHashing("") }},
		{name: "sticky-sessions", make: func() strategyFn { return strategyStickySessions("") }},
	}

	for _, benchmark := range benchmarks {
		b.Run(benchmark.name, func(b *testing.B) {
			selectProxy := benchmark.make()
			b.ReportAllocs()
			b.ResetTimer()
			for i := 0; i < b.N; i++ {
				selectProxy(proxies, metadata, true)
			}
		})
	}
}

func TestWeightedBytesColdStartAvoidsHerd(t *testing.T) {
	now := time.Date(2026, time.September, 27, 0, 0, 0, 0, time.UTC)
	strategy := newWeightedBytesStrategy(map[string]int{"primary": 70, "secondary": 30}, func() time.Time { return now })
	proxies := selectionTestProxies("primary", "secondary")

	first := strategy.selectProxy(proxies, "", true)
	second := strategy.selectProxy(proxies, "", true)
	if first.Name() != "primary" {
		t.Fatalf("cold-start first flow selected %q, want primary", first.Name())
	}
	if second.Name() != "secondary" {
		t.Fatalf("cold-start second flow selected %q, want secondary after the first flow reservation", second.Name())
	}
}

func TestWeightedBytesSevenToThreeEqualFlows(t *testing.T) {
	now := time.Date(2026, time.September, 27, 0, 0, 0, 0, time.UTC)
	strategy := newWeightedBytesStrategy(map[string]int{"primary": 70, "secondary": 30}, func() time.Time { return now })
	proxies := selectionTestProxies("primary", "secondary")
	counts := map[string]int{}

	for i := 0; i < 10; i++ {
		selected := strategy.selectProxy(proxies, "", true)
		counts[selected.Name()]++
		strategy.recordBytes(selected.Name(), 90_000, 10_000, now)
	}
	if counts["primary"] != 7 || counts["secondary"] != 3 {
		t.Fatalf("equal-sized flows split %v, want 7:3", counts)
	}
	primaryUpload, primaryDownload := strategy.windowBytes("primary", now)
	secondaryUpload, secondaryDownload := strategy.windowBytes("secondary", now)
	if primaryUpload+primaryDownload != 700_000 || secondaryUpload+secondaryDownload != 300_000 {
		t.Fatalf("byte totals are primary=%d secondary=%d, want 700000:300000", primaryUpload+primaryDownload, secondaryUpload+secondaryDownload)
	}
}

func TestWeightedBytesUnequalFlowsFollowByteDeficit(t *testing.T) {
	now := time.Date(2026, time.September, 27, 0, 0, 0, 0, time.UTC)
	strategy := newWeightedBytesStrategy(map[string]int{"primary": 70, "secondary": 30}, func() time.Time { return now })
	proxies := selectionTestProxies("primary", "secondary")

	// These unequal completed flows leave primary well below its 70% target.
	strategy.recordBytes("primary", 4_000_000, 0, now)
	strategy.recordBytes("secondary", 6_000_000, 0, now)
	if got := strategy.selectProxy(proxies, "", false).Name(); got != "primary" {
		t.Fatalf("after 4 MB vs 6 MB, selected %q; want under-target primary", got)
	}

	// A larger follow-up flow counts by bytes, not by number of flow opens.
	strategy.recordBytes("primary", 12_000_000, 0, now)
	if got := strategy.selectProxy(proxies, "", false).Name(); got != "secondary" {
		t.Fatalf("after primary receives a 12 MB flow, selected %q; want under-target secondary", got)
	}
}

func TestWeightedBytesSlidingWindowExpiry(t *testing.T) {
	start := time.Date(2026, time.September, 27, 0, 0, 0, 0, time.UTC)
	strategy := newWeightedBytesStrategy(nil, func() time.Time { return start })
	strategy.recordBytes("wan-a", 123, 456, start)
	strategy.recordBytes("wan-a", 1_000, 2_000, start.Add(29*time.Second))

	upload, download := strategy.windowBytes("wan-a", start.Add(29*time.Second))
	if upload != 1_123 || download != 2_456 {
		t.Fatalf("before expiry got upload=%d download=%d, want 1123/2456", upload, download)
	}

	strategy.recordBytes("wan-a", 9, 11, start.Add(30*time.Second))
	upload, download = strategy.windowBytes("wan-a", start.Add(30*time.Second))
	if upload != 1_009 || download != 2_011 {
		t.Fatalf("at the 30 second boundary got upload=%d download=%d, want old bucket expired (1009/2011)", upload, download)
	}
}

func TestWeightedBytesConcurrentTrafficCounters(t *testing.T) {
	now := time.Date(2026, time.September, 27, 0, 0, 0, 0, time.UTC)
	strategy := newWeightedBytesStrategy(nil, func() time.Time { return now })
	const reports = 256
	var group sync.WaitGroup
	group.Add(reports)
	for i := 0; i < reports; i++ {
		go func() {
			defer group.Done()
			strategy.recordBytes("wan-a", 17, 31, now)
		}()
	}
	group.Wait()

	upload, download := strategy.windowBytes("wan-a", now)
	if upload != reports*17 || download != reports*31 {
		t.Fatalf("concurrent counter totals got upload=%d download=%d, want %d/%d", upload, download, reports*17, reports*31)
	}
}

func TestWeightedBytesDynamicWeightUpdate(t *testing.T) {
	now := time.Date(2026, time.September, 27, 0, 0, 0, 0, time.UTC)
	strategy := newWeightedBytesStrategy(map[string]int{"primary": 70, "secondary": 30}, func() time.Time { return now })
	proxies := selectionTestProxies("primary", "secondary")
	for i := 0; i < 10; i++ {
		strategy.selectProxy(proxies, "", true)
	}

	if err := strategy.setWeights(map[string]int{"primary": 30, "secondary": 70}); err != nil {
		t.Fatalf("valid live weight update failed: %v", err)
	}
	if got := strategy.selectProxy(proxies, "", false).Name(); got != "secondary" {
		t.Fatalf("after changing weights to 3:7, selected %q; want secondary", got)
	}
	if err := strategy.setWeights(map[string]int{"primary": 0, "secondary": 0}); err == nil {
		t.Fatal("accepted a live weight update with no positive target weight")
	}
	if got := strategy.selectProxy(proxies, "", false).Name(); got != "secondary" {
		t.Fatalf("invalid weight update changed active strategy; next selection was %q", got)
	}
}

func TestWeightedBytesUnhealthyProxyRecoveryWarmup(t *testing.T) {
	now := time.Date(2026, time.September, 27, 0, 0, 0, 0, time.UTC)
	strategy := newWeightedBytesStrategy(map[string]int{"primary": 70, "secondary": 30}, func() time.Time { return now })
	primary := &selectionTestProxy{name: "primary", alive: true}
	secondary := &selectionTestProxy{name: "secondary", alive: false}
	proxies := []C.Proxy{primary, secondary}

	for i := 0; i < 4; i++ {
		if got := strategy.selectProxy(proxies, "", true); got != primary {
			t.Fatalf("selected unhealthy proxy %q", got.Name())
		}
	}

	secondary.alive = true
	strategy.selectProxy(proxies, "", false) // Record recovery and begin warmup.
	var earlyRecoverySelections int
	for i := 0; i < 8; i++ {
		if got := strategy.selectProxy(proxies, "", true); got == secondary {
			earlyRecoverySelections++
		}
	}

	now = now.Add(weightedBytesWarmup)
	var warmedRecoverySelections int
	for i := 0; i < 8; i++ {
		if got := strategy.selectProxy(proxies, "", true); got == secondary {
			warmedRecoverySelections++
		}
	}
	if warmedRecoverySelections <= earlyRecoverySelections {
		t.Fatalf("recovered proxy share did not increase after warmup: early=%d warmed=%d", earlyRecoverySelections, warmedRecoverySelections)
	}
}

func TestWeightedBytesBurstReservationsDoNotHerd(t *testing.T) {
	now := time.Date(2026, time.September, 27, 0, 0, 0, 0, time.UTC)
	strategy := newWeightedBytesStrategy(map[string]int{"primary": 70, "secondary": 30}, func() time.Time { return now })
	proxies := selectionTestProxies("primary", "secondary")
	const flows = 16
	start := make(chan struct{})
	results := make(chan string, flows)
	var group sync.WaitGroup
	group.Add(flows)
	for i := 0; i < flows; i++ {
		go func() {
			defer group.Done()
			<-start
			results <- strategy.selectProxy(proxies, "", true).Name()
		}()
	}
	close(start)
	group.Wait()
	close(results)

	counts := map[string]int{}
	for name := range results {
		counts[name]++
	}
	if counts["primary"] < 10 || counts["primary"] > 12 || counts["secondary"] < 4 || counts["secondary"] > 6 {
		t.Fatalf("burst reservations split %v, want approximately 7:3", counts)
	}
}

func TestWeightedBytesTCPAndUDPAffinity(t *testing.T) {
	strategy := newWeightedBytesStrategy(map[string]int{"primary": 50, "secondary": 50}, time.Now)
	proxies := selectionTestProxies("primary", "secondary")
	group := NewGroupBase(GroupBaseOption{Name: "weighted-test", Type: C.LoadBalance, EmptyFallback: proxies[0]})
	group.providerProxies = proxies
	lb := &LoadBalance{GroupBase: group, byteStrategy: strategy, strategyFn: strategy.strategy("")}
	metadata := &C.Metadata{}

	firstTCP, err := lb.DialContext(context.Background(), metadata)
	if err != nil {
		t.Fatal(err)
	}
	firstTCPRoute := firstTCP.RemoteDestination()
	if _, err := firstTCP.Write([]byte("tcp flow bytes")); err != nil {
		t.Fatal(err)
	}
	secondTCP, err := lb.DialContext(context.Background(), metadata)
	if err != nil {
		t.Fatal(err)
	}
	if firstTCP.RemoteDestination() != firstTCPRoute {
		t.Fatalf("open TCP flow moved from %q to %q", firstTCPRoute, firstTCP.RemoteDestination())
	}
	if secondTCP.RemoteDestination() == firstTCPRoute {
		t.Fatalf("new TCP flow reused %q despite its observed bytes", firstTCPRoute)
	}

	firstUDP, err := lb.ListenPacketContext(context.Background(), metadata)
	if err != nil {
		t.Fatal(err)
	}
	firstUDPRoute := firstUDP.RemoteDestination()
	if _, err := firstUDP.WriteTo([]byte("udp flow bytes"), &net.UDPAddr{}); err != nil {
		t.Fatal(err)
	}
	secondUDP, err := lb.ListenPacketContext(context.Background(), metadata)
	if err != nil {
		t.Fatal(err)
	}
	if firstUDP.RemoteDestination() != firstUDPRoute {
		t.Fatalf("open UDP flow moved from %q to %q", firstUDPRoute, firstUDP.RemoteDestination())
	}
	if secondUDP.RemoteDestination() == firstUDPRoute {
		t.Fatalf("new UDP flow reused %q despite its observed bytes", firstUDPRoute)
	}
	primaryProxy := proxies[0].(*selectionTestProxy)
	secondaryProxy := proxies[1].(*selectionTestProxy)
	if primaryProxy.dials+secondaryProxy.dials != 2 || primaryProxy.packets+secondaryProxy.packets != 2 {
		t.Fatalf("flows opened more than once per transport: dials=%d packets=%d", primaryProxy.dials+secondaryProxy.dials, primaryProxy.packets+secondaryProxy.packets)
	}
}

func BenchmarkWeightedBytesSelection(b *testing.B) {
	for _, proxyCount := range []int{2, 8, 32} {
		b.Run(fmt.Sprintf("proxies_%d", proxyCount), func(b *testing.B) {
			names := make([]string, proxyCount)
			weights := make(map[string]int, proxyCount)
			for index := 0; index < proxyCount; index++ {
				name := fmt.Sprintf("wan-%02d", index)
				names[index] = name
				weights[name] = 100 / proxyCount
				if index < 100%proxyCount {
					weights[name]++
				}
			}
			proxies := selectionTestProxies(names...)
			now := time.Date(2026, time.September, 27, 0, 0, 0, 0, time.UTC)
			strategy := newWeightedBytesStrategy(weights, func() time.Time { return now })
			for i := 0; i < proxyCount*2; i++ {
				selected := strategy.selectProxy(proxies, "", true)
				strategy.recordBytes(selected.Name(), 32*1024, 32*1024, now)
			}
			b.ReportAllocs()
			b.ResetTimer()
			for index := 0; index < b.N; index++ {
				selected := strategy.selectProxy(proxies, "", true)
				strategy.recordBytes(selected.Name(), 32*1024, 32*1024, now)
			}
		})
	}
}
