//go:build windows && amd64

package main

import (
	"bytes"
	"context"
	"os"
	"testing"
	"time"
)

// Deliberate local hardware acceptance only. Ordinary go test and CI never
// attach a device. Cleanup uses only each backend's retained random identity.
func TestLiveMixedControllerAcceptance(t *testing.T) {
	if os.Getenv("AKM_LIVE_CONTROLLER_ACCEPTANCE") != "mixed" {
		t.Skip("requires explicit local controller acceptance run")
	}
	baseline, err := readXInputSlots()
	if err != nil {
		t.Fatal(err)
	}
	for _, slot := range baseline {
		if slot.present {
			t.Fatal("acceptance requires free XInput slots; no device was changed")
		}
	}
	type liveDevice struct {
		backend outputBackend
		closed  bool
	}
	var devices []*liveDevice
	closeDevice := func(d *liveDevice) {
		neutralCtx, neutralCancel := context.WithTimeout(context.Background(), 200*time.Millisecond)
		if err := d.backend.Neutral(neutralCtx); err != nil {
			t.Errorf("neutral: %v", err)
		}
		neutralCancel()
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		if err := d.backend.Close(ctx); err != nil {
			t.Errorf("owned removal: %v", err)
			return
		}
		d.closed = true
	}
	t.Cleanup(func() {
		for _, d := range devices {
			if !d.closed {
				closeDevice(d)
			}
		}
		after, err := readXInputSlots()
		if err != nil || after != baseline {
			t.Errorf("XInput baseline not restored: %+v / %v", after, err)
		}
	})
	connect := func(backend outputBackend) *liveDevice {
		d := &liveDevice{backend: backend}
		devices = append(devices, d)
		ctx, cancel := context.WithTimeout(context.Background(), 12*time.Second)
		defer cancel()
		started := time.Now()
		if err := backend.Connect(ctx); err != nil {
			t.Fatalf("%T connect: %v", backend, err)
		}
		t.Logf("%T connected with neutral startup in %s", backend, time.Since(started))
		return d
	}
	// First establish actual neutral HID enumeration/readback/removal before
	// allowing nonneutral test packets on any DualSense instance.
	dsProbe := newDualSenseProbeBackend().(*dualSenseUSBBackend)
	probe := connect(dsProbe)
	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	started := time.Now()
	samples := 0
	for time.Since(started) < 500*time.Millisecond {
		report, err := dsProbe.observer.Read(ctx)
		if err != nil {
			cancel()
			t.Fatal(err)
		}
		if !neutralDualSenseReport(report) {
			cancel()
			t.Fatalf("nonneutral startup report: %x", report)
		}
		samples++
	}
	cancel()
	t.Logf("DualSense: %d neutral HID samples across %s", samples, time.Since(started))
	closeDevice(probe)
	if t.Failed() {
		t.FailNow()
	}

	x1 := newXboxRuntimeBackend().(*usbBackend)
	x1d := connect(x1)
	p1 := packet{Buttons: 0x1000, LT: 37, LX: 12000, RY: -14000}
	assertLivePacket(t, x1, p1)
	d1 := newDualSenseProbeBackend().(*dualSenseUSBBackend)
	d1d := connect(d1)
	assertLiveObserved(t, x1, p1)
	d1.neutralOnly = false // test-only permission after the real neutral probe
	p2 := packet{Buttons: 0x2000, RT: 91, LY: 23000, RX: -8000}
	assertLivePacket(t, d1, p2)
	x2 := newXboxRuntimeBackend().(*usbBackend)
	x2d := connect(x2)
	assertLiveObserved(t, x1, p1)
	assertLiveObserved(t, d1, p2)
	p3 := packet{Buttons: 0x4000, LT: 111, RX: 16000}
	assertLivePacket(t, x2, p3)
	d2 := newDualSenseProbeBackend().(*dualSenseUSBBackend)
	d2d := connect(d2)
	assertLiveObserved(t, x1, p1)
	assertLiveObserved(t, d1, p2)
	assertLiveObserved(t, x2, p3)
	d2.neutralOnly = false
	p4 := packet{Buttons: 0x8000, RT: 201, LX: -27000}
	assertLivePacket(t, d2, p4)
	for i := 0; i < 20; i++ {
		assertLivePacket(t, x1, p1)
		assertLivePacket(t, d1, p2)
		assertLivePacket(t, x2, p3)
		assertLivePacket(t, d2, p4)
		time.Sleep(5 * time.Millisecond)
	}
	t.Log("two Xbox and two DualSense outputs preserve distinct buttons, triggers and axes")
	closeDevice(d1d)
	assertLiveObserved(t, x1, p1)
	assertLiveObserved(t, x2, p3)
	assertLiveObserved(t, d2, p4)
	closeDevice(x1d)
	assertLiveObserved(t, x2, p3)
	assertLiveObserved(t, d2, p4)
	t.Log("removing either type leaves active peers unchanged")
	closeDevice(x2d)
	closeDevice(d2d)
}

func assertLivePacket(t *testing.T, backend outputBackend, want packet) {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 500*time.Millisecond)
	defer cancel()
	if err := backend.Submit(ctx, want); err != nil {
		t.Fatal(err)
	}
	assertLiveObserved(t, backend, want)
}

func assertLiveObserved(t *testing.T, backend outputBackend, want packet) {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 500*time.Millisecond)
	defer cancel()
	// Sample AFTER Submit (or a peer lifecycle change), so a report generated
	// during native identity verification cannot masquerade as fresh readback.
	var stamp uint32
	if d, ok := backend.(*dualSenseUSBBackend); ok {
		d.pad.mu.Lock()
		stamp = d.pad.lastReportStamp
		d.pad.mu.Unlock()
	}
	switch b := backend.(type) {
	case *usbBackend:
		for {
			states, err := readXInputSlots()
			if err != nil {
				t.Fatal(err)
			}
			if states[b.slot].present && states[b.slot].value == want {
				return
			}
			select {
			case <-ctx.Done():
				t.Fatalf("Xbox slot %d: got %+v; want %+v", b.slot, states[b.slot], want)
			case <-time.After(time.Millisecond):
			}
		}
	case *dualSenseUSBBackend:
		expected, err := newDualSensePad()
		if err != nil {
			t.Fatal(err)
		}
		if err = expected.setPacket(want); err != nil {
			t.Fatal(err)
		}
		wantReport := expected.HandleTransfer(4, 1, nil)
		for {
			report, err := b.observer.Read(ctx)
			if err != nil {
				t.Fatal(err)
			}
			if !dualSenseReportAfter(report, stamp) {
				continue
			}
			if len(wantReport) != 64 || !bytes.Equal(report[1:7], wantReport[1:7]) || !bytes.Equal(report[8:11], wantReport[8:11]) {
				t.Fatalf("DualSense gameplay mismatch: got %x; want %x", report, wantReport)
			}
			return
		}
	default:
		t.Fatalf("unexpected backend %T", backend)
	}
}
