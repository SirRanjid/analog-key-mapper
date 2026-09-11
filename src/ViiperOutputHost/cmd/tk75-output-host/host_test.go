package main

import (
	"bufio"
	"context"
	"encoding/base64"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"strings"
	"sync"
	"testing"
	"time"
)

func TestExclusiveXboxAttachmentPolicy(t *testing.T) {
	// Exercise the complete gate truth table: accepted real input still cannot
	// enter a shared driver, and an unreviewed ABI is never allowed.
	for bits := 0; bits < 16; bits++ {
		neutral, exclusive, abi, accepted := bits&1 != 0, bits&2 != 0, bits&4 != 0, bits&8 != 0
		allowed := xboxAttachmentAllowed(neutral, exclusive, abi, accepted)
		expected := bits == 7 || bits == 14 || bits == 15
		if allowed != expected {
			t.Fatalf("policy %04b allowed=%v, expected=%v", bits, allowed, expected)
		}
	}
}

// These tests inject a fake backend. They never invoke newUSBBackend,
// attachNative, XInput, ListenAndServe, or any real controller path.
type fakeBackend struct {
	mu                                              sync.Mutex
	operations                                      []string
	packets                                         []packet
	failConnect, failSubmit, failNeutral, failClose bool
	hangConnect                                     bool
	closed                                          chan struct{}
	closeOnce                                       sync.Once
}

func newFake() *fakeBackend { return &fakeBackend{closed: make(chan struct{})} }
func (b *fakeBackend) record(op string) {
	b.mu.Lock()
	b.operations = append(b.operations, op)
	b.mu.Unlock()
}
func (b *fakeBackend) Connect(ctx context.Context) error {
	b.record("connect")
	if b.hangConnect {
		<-ctx.Done()
		return ctx.Err()
	}
	if b.failConnect {
		return errors.New("fake connect failed")
	}
	return nil
}
func (b *fakeBackend) Submit(ctx context.Context, p packet) error {
	b.record("submit")
	if b.failSubmit {
		return errors.New("fake submit failed")
	}
	b.mu.Lock()
	b.packets = append(b.packets, p)
	b.mu.Unlock()
	return nil
}
func (b *fakeBackend) Neutral(ctx context.Context) error {
	b.record("neutral")
	b.mu.Lock()
	b.packets = append(b.packets, packet{})
	b.mu.Unlock()
	if b.failNeutral {
		return errors.New("fake neutral failed")
	}
	return nil
}
func (b *fakeBackend) Close(ctx context.Context) error {
	b.record("close")
	b.closeOnce.Do(func() { close(b.closed) })
	if b.failClose {
		return errors.New("fake close failed")
	}
	return nil
}
func (b *fakeBackend) snapshot() ([]string, []packet) {
	b.mu.Lock()
	defer b.mu.Unlock()
	return append([]string{}, b.operations...), append([]packet{}, b.packets...)
}

type fixture struct {
	input       *io.PipeWriter
	output      *bufio.Reader
	done        chan int
	closeOutput *io.PipeReader
}

func startFixture(t *testing.T, b *fakeBackend, limits hostLimits) *fixture {
	t.Helper()
	inR, inW := io.Pipe()
	outR, outW := io.Pipe()
	done := make(chan int, 1)
	go func() {
		done <- runHost(inR, outW, func() outputBackend { return b }, limits)
		outW.Close()
		inR.Close()
	}()
	f := &fixture{inW, bufio.NewReader(outR), done, outR}
	t.Cleanup(func() { inW.Close(); outR.Close() })
	return f
}
func (f *fixture) exchange(t *testing.T, line string) string {
	t.Helper()
	if _, err := io.WriteString(f.input, line+"\n"); err != nil {
		t.Fatal(err)
	}
	return f.reply(t)
}
func (f *fixture) reply(t *testing.T) string {
	t.Helper()
	done := make(chan string, 1)
	go func() {
		line, err := f.output.ReadString('\n')
		if err != nil {
			done <- "READ ERROR: " + err.Error()
			return
		}
		done <- strings.TrimSuffix(line, "\n")
	}()
	select {
	case reply := <-done:
		return reply
	case <-time.After(3 * time.Second):
		t.Fatal("protocol reply timed out")
		return ""
	}
}
func (f *fixture) exit(t *testing.T, want int) {
	t.Helper()
	select {
	case got := <-f.done:
		if got != want {
			t.Fatalf("exit %d want %d", got, want)
		}
	case <-time.After(3 * time.Second):
		t.Fatal("host failed to exit")
	}
}
func requireAck(t *testing.T, reply string, id int) {
	t.Helper()
	want := fmt.Sprintf("TK75OUT/1 %d ACK", id)
	if reply != want {
		t.Fatalf("reply %q want %q", reply, want)
	}
}
func requireError(t *testing.T, reply string) {
	t.Helper()
	if !strings.Contains(reply, " ERROR ") {
		t.Fatalf("expected ERROR, got %q", reply)
	}
}
func awaitClosed(t *testing.T, b *fakeBackend) {
	t.Helper()
	select {
	case <-b.closed:
	case <-time.After(time.Second):
		t.Fatal("cleanup did not close fake backend")
	}
}

func TestPacketGoldenAndValidation(t *testing.T) {
	id, op, p, err := parseCommand("TK75OUT/1 42 FRAME 62463 255 128 -32768 32767 -1 0", 41)
	if err != nil || id != 42 || op != "FRAME" || p != (packet{62463, 255, 128, -32768, 32767, -1, 0}) {
		t.Fatalf("golden frame: %#v %v", p, err)
	}
	bad := []string{"", "TK75OUT/2 1 CONNECT", "TK75OUT/1 0 CONNECT", "TK75OUT/1 +1 CONNECT", "TK75OUT/1 -1 CONNECT", "TK75OUT/1 9223372036854775808 CONNECT", "TK75OUT/1 1 CONNECT extra", "TK75OUT/1 1 FRAME 0 0 0 0 0 0", "TK75OUT/1 1 FRAME 1024 0 0 0 0 0 0", "TK75OUT/1 1 FRAME 2048 0 0 0 0 0 0", "TK75OUT/1 1 FRAME 65536 0 0 0 0 0 0", "TK75OUT/1 1 FRAME 0 256 0 0 0 0 0", "TK75OUT/1 1 FRAME 0 0 -1 0 0 0 0", "TK75OUT/1 1 FRAME 0 0 0 -32769 0 0 0", "TK75OUT/1 1 FRAME 0 0 0 0 32768 0 0", "TK75OUT/1 1 FRAME 0 0 0 0 0 NaN 0", "TK75OUT/1 1 FRAME 0 0 0 0 0 0 1.0", "TK75OUT/1 1 FRAME 0  0 0 0 0 0 0"}
	for _, line := range bad {
		t.Run(line, func(t *testing.T) {
			if _, _, _, err := parseCommand(line, 0); err == nil {
				t.Fatal("malformed command accepted")
			}
		})
	}
	if _, _, _, err := parseCommand("TK75OUT/1 42 NEUTRAL", 42); err == nil {
		t.Fatal("duplicate id accepted")
	}
}

func TestBoundedInput(t *testing.T) {
	for _, text := range []string{strings.Repeat("x", 513) + "\n", "TK75OUT/1 1 CONNECT", "TK75OUT/1 1 CONNECT\x00\n", "ä\n"} {
		if _, err := readBounded(bufio.NewReader(strings.NewReader(text))); err == nil {
			t.Fatalf("accepted %q", text)
		}
	}
	if line, err := readBounded(bufio.NewReader(strings.NewReader("TK75OUT/1 1 QUIT\r\n"))); err != nil || line != "TK75OUT/1 1 QUIT" {
		t.Fatal(line, err)
	}
}

func TestOrderedLifecycleAndIndependentPacketFields(t *testing.T) {
	b := newFake()
	f := startFixture(t, b, normalLimits)
	requireAck(t, f.exchange(t, "TK75OUT/1 1 CONNECT"), 1)
	requireAck(t, f.exchange(t, "TK75OUT/1 2 FRAME 4096 17 201 -32768 32767 -123 456"), 2)
	requireAck(t, f.exchange(t, "TK75OUT/1 3 FRAME 0 0 0 0 0 0 0"), 3)
	requireAck(t, f.exchange(t, "TK75OUT/1 4 NEUTRAL"), 4)
	requireAck(t, f.exchange(t, "TK75OUT/1 5 QUIT"), 5)
	f.exit(t, 0)
	ops, packets := b.snapshot()
	if strings.Join(ops, ",") != "connect,neutral,submit,submit,neutral,neutral,close" {
		t.Fatal(ops)
	}
	if packets[1] != (packet{4096, 17, 201, -32768, 32767, -123, 456}) || packets[2] != (packet{}) || packets[len(packets)-1] != (packet{}) {
		t.Fatal(packets)
	}
}

func TestMalformedAndDuplicateFailClosed(t *testing.T) {
	for _, line := range []string{"TK75OUT/1 1 FRAME 0 0 0 0 0 0 0", "TK75OUT/1 2 FRAME 1024 0 0 0 0 0 0", "TK75OUT/1 2 UNKNOWN", "TK75OUT/1 2 CONNECT", "TK75OUT/1 2 FRAME 0 0 0 32768 0 0 0"} {
		t.Run(line, func(t *testing.T) {
			b := newFake()
			f := startFixture(t, b, normalLimits)
			requireAck(t, f.exchange(t, "TK75OUT/1 1 CONNECT"), 1)
			requireError(t, f.exchange(t, line))
			f.exit(t, 1)
			awaitClosed(t, b)
			_, packets := b.snapshot()
			if packets[len(packets)-1] != (packet{}) {
				t.Fatal("last state not neutral")
			}
		})
	}
}

func TestFrameBeforeConnectDoesNotCreateBackend(t *testing.T) {
	b := newFake()
	f := startFixture(t, b, normalLimits)
	requireError(t, f.exchange(t, "TK75OUT/1 1 FRAME 0 0 0 0 0 0 0"))
	f.exit(t, 1)
	ops, _ := b.snapshot()
	if len(ops) != 0 {
		t.Fatal(ops)
	}
}

func TestBackendErrorsNeverAckAndAlwaysClose(t *testing.T) {
	for _, kind := range []string{"connect", "submit", "neutral", "close"} {
		t.Run(kind, func(t *testing.T) {
			b := newFake()
			if kind == "connect" {
				b.failConnect = true
			}
			f := startFixture(t, b, normalLimits)
			reply := f.exchange(t, "TK75OUT/1 1 CONNECT")
			if kind == "connect" {
				requireError(t, reply)
			} else {
				requireAck(t, reply, 1)
				switch kind {
				case "submit":
					b.failSubmit = true
					reply = f.exchange(t, "TK75OUT/1 2 FRAME 4096 0 0 0 0 0 0")
				case "neutral":
					b.failNeutral = true
					reply = f.exchange(t, "TK75OUT/1 2 QUIT")
				case "close":
					b.failClose = true
					reply = f.exchange(t, "TK75OUT/1 2 QUIT")
				}
				requireError(t, reply)
			}
			f.exit(t, 1)
			awaitClosed(t, b)
		})
	}
}

func TestWatchdogAndHeartbeat(t *testing.T) {
	b := newFake()
	f := startFixture(t, b, normalLimits)
	requireAck(t, f.exchange(t, "TK75OUT/1 1 CONNECT"), 1)
	for id := 2; id < 8; id++ {
		time.Sleep(55 * time.Millisecond)
		requireAck(t, f.exchange(t, fmt.Sprintf("TK75OUT/1 %d FRAME 4096 0 0 123 0 0 0", id)), id)
	}
	select {
	case <-b.closed:
		t.Fatal("valid heartbeat was expired")
	default:
	}
	awaitClosed(t, b)
	requireError(t, f.reply(t))
	f.exit(t, 1)
	_, packets := b.snapshot()
	if packets[len(packets)-1] != (packet{}) {
		t.Fatal("watchdog did not neutralize")
	}
}

func TestEOFWhileHeldNeutralizesAndCloses(t *testing.T) {
	b := newFake()
	f := startFixture(t, b, normalLimits)
	requireAck(t, f.exchange(t, "TK75OUT/1 1 CONNECT"), 1)
	requireAck(t, f.exchange(t, "TK75OUT/1 2 FRAME 4096 255 0 123 0 0 0"), 2)
	f.input.Close()
	awaitClosed(t, b)
	f.exit(t, 0)
	_, p := b.snapshot()
	if p[len(p)-1] != (packet{}) {
		t.Fatal(p)
	}
}

func TestConnectDeadlineRollsBack(t *testing.T) {
	b := newFake()
	b.hangConnect = true
	limits := normalLimits
	limits.connect = 40 * time.Millisecond
	f := startFixture(t, b, limits)
	requireError(t, f.exchange(t, "TK75OUT/1 1 CONNECT"))
	f.exit(t, 1)
	awaitClosed(t, b)
}

type blockedWriter struct {
	entered chan struct{}
	unblock chan struct{}
	once    sync.Once
}

func (w *blockedWriter) Write(p []byte) (int, error) {
	w.once.Do(func() { close(w.entered) })
	<-w.unblock
	return len(p), nil
}
func TestBlockedStdoutDoesNotBlockWatchdogCleanup(t *testing.T) {
	b := newFake()
	inR, inW := io.Pipe()
	w := &blockedWriter{entered: make(chan struct{}), unblock: make(chan struct{})}
	done := make(chan int, 1)
	go func() { done <- runHost(inR, w, func() outputBackend { return b }, normalLimits) }()
	io.WriteString(inW, "TK75OUT/1 1 CONNECT\n")
	<-w.entered
	awaitClosed(t, b)
	close(w.unblock)
	inW.Close()
	inR.Close()
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("host did not stop")
	}
}

func TestDualSenseNeutralAndGoldenControls(t *testing.T) {
	neutral, err := encodeDualSense(packet{})
	if err != nil {
		t.Fatal(err)
	}
	var wantNeutral [33]byte
	wantNeutral[32] = 0xE0 // stationary -1g, no movement/contact/press
	if neutral != wantNeutral {
		t.Fatalf("neutral wire state %x", neutral)
	}
	p := packet{Buttons: 0x1219, LT: 31, RT: 255, LX: -32768, LY: 32767, RX: 32767, RY: -32768}
	encoded, err := encodeDualSense(p)
	if err != nil {
		t.Fatal(err)
	}
	// A + RB + Start + Up/Right => Cross + R1 + Options + both trigger bits.
	if encoded[0] != 0x80 || encoded[1] != 0x80 || encoded[2] != 0x7F || encoded[3] != 0x7F ||
		binary.LittleEndian.Uint32(encoded[4:8]) != 0x2E20 || encoded[8] != 9 || encoded[9] != 31 || encoded[10] != 255 {
		t.Fatalf("golden control state %x", encoded)
	}
	for _, bit := range []uint16{0x0400, 0x0800} {
		if _, err := encodeDualSense(packet{Buttons: bit}); err == nil {
			t.Fatalf("reserved bit %x accepted", bit)
		}
	}
}

func TestDualSenseButtonIdentityAndHatConflicts(t *testing.T) {
	for _, item := range []struct {
		xinput    uint16
		dualsense uint32
	}{
		{0x1000, 0x20}, {0x2000, 0x40}, {0x4000, 0x10}, {0x8000, 0x80},
		{0x100, 0x100}, {0x200, 0x200}, {0x20, 0x1000}, {0x10, 0x2000}, {0x40, 0x4000}, {0x80, 0x8000},
	} {
		got, err := encodeDualSense(packet{Buttons: item.xinput})
		if err != nil || binary.LittleEndian.Uint32(got[4:8]) != item.dualsense {
			t.Fatalf("button identity %x: %x %v", item.xinput, got, err)
		}
		if got[8] != 0 || got[9] != 0 || got[10] != 0 {
			t.Fatal("button crossed into hat/trigger")
		}
	}
	for value, expected := range []byte{0, 1, 2, 0, 4, 5, 6, 4, 8, 9, 10, 8, 0, 1, 2, 0} {
		got, _ := encodeDualSense(packet{Buttons: uint16(value)})
		if got[8] != expected || binary.LittleEndian.Uint32(got[4:8]) != 0 {
			t.Fatalf("hat combination %x: %x", value, got)
		}
	}
}

func TestDualSenseAxesPreserveCenterEndpointsAndMonotonicity(t *testing.T) {
	if dualSenseAxis(0, false) != 0 || dualSenseAxis(0, true) != 0 ||
		dualSenseAxis(-32768, false) != -128 || dualSenseAxis(32767, false) != 127 ||
		dualSenseAxis(-32768, true) != 127 || dualSenseAxis(32767, true) != -128 {
		t.Fatal("axis endpoints or neutral changed")
	}
	last, invertedLast := -128, 127
	seen, invertedSeen := map[int]bool{}, map[int]bool{}
	for v := -32768; v <= 32767; v++ {
		x, y := int(dualSenseAxis(int16(v), false)), int(dualSenseAxis(int16(v), true))
		if x < last || y > invertedLast || x-last > 1 || invertedLast-y > 1 {
			t.Fatalf("axis discontinuity at %d", v)
		}
		if v < 0 && (x > 0 || y < 0) || v > 0 && (x < 0 || y > 0) {
			t.Fatalf("axis sign at %d", v)
		}
		seen[x], invertedSeen[y] = true, true
		last, invertedLast = x, y
	}
	if len(seen) != 256 || len(invertedSeen) != 256 {
		t.Fatal("axis lost representable positions")
	}
}

func TestDualSenseReportNeutralMeansCenteredNotZero(t *testing.T) {
	var report [64]byte
	if neutralDualSenseReport(report[:]) {
		t.Fatal("zero report accepted as neutral")
	}
	report[0], report[1], report[2], report[3], report[4] = 1, 128, 128, 128, 128
	report[8], report[27], report[33], report[37] = 8, 0xE0, 0x80, 0x80
	if !neutralDualSenseReport(report[:]) {
		t.Fatal("stationary centered report rejected")
	}
	report[7], report[28], report[49], report[53] = 5, 93, 0x10, 0x2A
	if !neutralDualSenseReport(report[:]) {
		t.Fatal("ordinary sequence/status bytes treated as input")
	}
	for _, index := range []int{1, 2, 3, 4, 5, 6, 8, 9, 10, 16, 18, 20, 22, 24, 26} {
		changed := report
		changed[index] ^= 1
		if neutralDualSenseReport(changed[:]) {
			t.Fatalf("nonneutral field %d accepted", index)
		}
	}
	for _, index := range []int{33, 37} {
		changed := report
		changed[index] &^= 0x80
		if neutralDualSenseReport(changed[:]) {
			t.Fatal("touch contact accepted as neutral")
		}
	}
	if neutralDualSenseReport(report[:63]) || neutralDualSenseReport(append(report[:], 0)) {
		t.Fatal("wrong report length accepted")
	}
}

func TestUnavailableDualSenseNeverAcknowledgesConnect(t *testing.T) {
	inR, inW := io.Pipe()
	outR, outW := io.Pipe()
	defer inR.Close()
	defer inW.Close()
	defer outR.Close()
	done := make(chan int, 1)
	go func() {
		done <- runHost(inR, outW, func() outputBackend { return &unavailableOutput{reason: dualSenseAcceptanceRequired} }, normalLimits)
		outW.Close()
	}()
	if _, err := io.WriteString(inW, "TK75OUT/1 1 CONNECT\n"); err != nil {
		t.Fatal(err)
	}
	line, err := bufio.NewReader(outR).ReadString('\n')
	if err != nil {
		t.Fatal(err)
	}
	parts := strings.Fields(line)
	if len(parts) != 4 || parts[0] != wireVersion || parts[1] != "1" || parts[2] != "ERROR" {
		t.Fatal(line)
	}
	message, err := base64.StdEncoding.DecodeString(parts[3])
	if err != nil || string(message) != dualSenseAcceptanceRequired {
		t.Fatalf("unclear missing prerequisite: %s", message)
	}
	select {
	case code := <-done:
		if code != 1 {
			t.Fatal(code)
		}
	case <-time.After(time.Second):
		t.Fatal("unavailable backend did not exit")
	}
}

// Only the pure predicate runs here. No SetupAPI, HID handle or native helper
// is imported into this already permitted protocol test boundary.
func TestOwnedDualSenseRequiresExactUSBSerialAndControllerAncestry(t *testing.T) {
	identity := ownedHIDIdentity{ControllerID: `ROOT\USBIP_WIN2\0000`, Serial: "T0123456789abcd"}
	usb := `USB\VID_054C&PID_0CE6\` + identity.Serial
	controller := identity.ControllerID
	for _, item := range []struct {
		name     string
		identity ownedHIDIdentity
		chain    []string
		want     bool
	}{
		{"own composite chain", identity, []string{`HID\OWN`, `USB\VID_054C&PID_0CE6&MI_03\CHILD`, usb, `USB\ROOT_HUB30\OWN`, controller, `HTREE\ROOT\0`}, true},
		{"case insensitive IDs", identity, []string{`hid\own`, strings.ToLower(usb), strings.ToLower(controller)}, true},
		{"physical device same model", identity, []string{`HID\PHYSICAL`, `USB\VID_054C&PID_0CE6\P0123456789ABCD`, controller}, false},
		{"different controller", identity, []string{`HID\OWN`, usb, `ROOT\USBIP_WIN2\0001`}, false},
		{"controller precedes serial", identity, []string{`HID\OWN`, controller, usb}, false},
		{"serial is not complete instance ID", identity, []string{`HID\OWN`, usb + "1", controller}, false},
		{"wrong product", identity, []string{`HID\OWN`, strings.Replace(usb, "0CE6", "0DF2", 1), controller}, false},
		{"wrong vendor", identity, []string{`HID\OWN`, strings.Replace(usb, "054C", "045E", 1), controller}, false},
		{"serial merely embedded in interface ID", identity, []string{`HID\OWN`, `HID\VID_054C&PID_0CE6\` + identity.Serial, controller}, false},
		{"controller prefix is not enough", identity, []string{`HID\OWN`, usb, controller + "X"}, false},
		{"missing controller", ownedHIDIdentity{Serial: identity.Serial}, []string{`HID\OWN`, usb, controller}, false},
		{"invalid serial character", ownedHIDIdentity{ControllerID: controller, Serial: "T0123456789abc!"}, []string{`HID\OWN`, `USB\VID_054C&PID_0CE6\T0123456789abc!`, controller}, false},
		{"missing HID leaf", identity, []string{usb, controller}, false},
		{"empty chain", identity, nil, false},
	} {
		t.Run(item.name, func(t *testing.T) {
			if got := item.identity.matchesAncestors(item.chain); got != item.want {
				t.Fatalf("ownership=%v, want %v", got, item.want)
			}
		})
	}
}

func TestDualSenseNeutralReadbackRequiresNewReportCounter(t *testing.T) {
	for _, item := range []struct {
		before, observed uint32
		want             bool
	}{
		{0, 1, true}, {17, 17, false}, {18, 17, false}, {17, 18, true},
		{0xFFFFFFFF, 0, true}, {0, 0xFFFFFFFF, false}, {0xFFFFFFFE, 2, true},
		{0, 0x80000000, false}, // ambiguous half-range must fail closed
	} {
		var report [64]byte
		report[0] = 1
		binary.LittleEndian.PutUint32(report[28:32], item.observed)
		if got := dualSenseReportAfter(report[:], item.before); got != item.want {
			t.Fatalf("counter %x after %x = %v", item.observed, item.before, got)
		}
	}
	var neutral [64]byte
	neutral[0], neutral[1], neutral[2], neutral[3], neutral[4] = 1, 128, 128, 128, 128
	neutral[8], neutral[27], neutral[33], neutral[37] = 8, 0xE0, 0x80, 0x80
	binary.LittleEndian.PutUint32(neutral[28:32], 100)
	if !neutralDualSenseReport(neutral[:]) || dualSenseReportAfter(neutral[:], 100) {
		t.Fatal("queued old neutral report would confirm a new reset")
	}
	binary.LittleEndian.PutUint32(neutral[28:32], 101)
	if !dualSenseReportAfter(neutral[:], 100) {
		t.Fatal("fresh neutral report rejected")
	}
	if dualSenseReportAfter(neutral[:63], 100) || dualSenseReportAfter(append(neutral[:], 0), 100) {
		t.Fatal("wrong size accepted as fresh report")
	}
	neutral[0] = 2
	if dualSenseReportAfter(neutral[:], 100) {
		t.Fatal("wrong report ID accepted")
	}
}
