//go:build windows && amd64

package main

import (
	"context"
	"encoding/binary"
	"errors"
	"strings"
	"testing"
)

func TestNativeSerialGenerationAndEarlyValidation(t *testing.T) {
	seen := make(map[string]bool)
	for i := 0; i < 64; i++ {
		serial, err := newNativeSerial()
		if err != nil || !validNativeSerial(serial) || serial[0] != 'T' || seen[serial] {
			t.Fatalf("invalid or repeated generated identity %q: %v", serial, err)
		}
		seen[serial] = true
	}
	for _, serial := range []string{"", "short", strings.Repeat("A", 14), strings.Repeat("A", 16), "INVALID-SERIAL!1", "Invalid\\Serial1", "\x00" + strings.Repeat("A", 14)} {
		attachment, err := attachNativeWithSerial(context.Background(), "127.0.0.1", 3241, "1-1", serial, false)
		if err == nil || attachment != nil {
			t.Fatalf("invalid serial %q reached a resource-owning attachment", serial)
		}
	}
}

func forbidNativeMutation(t *testing.T, calls []uint32) {
	t.Helper()
	for _, code := range calls {
		if code == nativeIOCTLAttachOnce || code == nativeIOCTLDetach || code == nativeIOCTLStopOwn {
			t.Fatalf("unexpected native mutation %#x", code)
		}
	}
}

func TestNativeMultipleControllersKeepIndependentFramesAndCleanup(t *testing.T) {
	ctx := context.Background()
	first := fakeOwnedNative()
	second := first
	second.BusID = "2-1"
	second.Serial = "OTHER0123456789"
	one := &fakeNativeTransport{}
	two := &fakeNativeTransport{shared: one}
	a, err := attachFakeNative(first, one)
	if err != nil {
		t.Fatal(err)
	}
	b, err := attachFakeNative(second, two)
	if err != nil {
		t.Fatal(err)
	}
	if a.ownership.Port == b.ownership.Port || len(one.items) != 2 {
		t.Fatal("attachments are not independent")
	}
	pa, err := newTrackedPad()
	if err != nil {
		t.Fatal(err)
	}
	pb, err := newTrackedPad()
	if err != nil {
		t.Fatal(err)
	}
	ba := &usbBackend{pad: pa, attachment: a, connected: true}
	bb := &usbBackend{pad: pb, attachment: b, connected: true}
	if err = ba.Submit(ctx, packet{Buttons: 0x1000, LT: 39, LX: 1234}); err != nil {
		t.Fatal(err)
	}
	if err = bb.Submit(ctx, packet{Buttons: 0x2000, RT: 87, RY: -4321}); err != nil {
		t.Fatal(err)
	}
	ra, rb := pa.HandleTransfer(1, 1, nil), pb.HandleTransfer(1, 1, nil)
	if len(ra) != 20 || len(rb) != 20 || binary.LittleEndian.Uint16(ra[2:4]) != 0x1000 || binary.LittleEndian.Uint16(rb[2:4]) != 0x2000 || ra[4] != 39 || rb[5] != 87 {
		t.Fatal("independent frame fields were mixed")
	}
	if err = a.Close(ctx); err != nil {
		t.Fatal(err)
	}
	if len(one.items) != 1 || one.items[0] != b.ownership || !one.closed || two.closed {
		t.Fatal("closing first attachment changed second")
	}
	if err = bb.Submit(ctx, packet{Buttons: 0x4000}); err != nil {
		t.Fatal("remaining controller stopped:", err)
	}
	if err = b.Close(ctx); err != nil || len(one.items) != 0 || !two.closed {
		t.Fatal("second cleanup failed:", err)
	}
}

func TestNativeAttachRejectsDuplicateOrCollidingLocationWithoutMutation(t *testing.T) {
	own := fakeOwnedNative()
	for _, mode := range []string{"same identity", "same location", "hash collision", "zero hash", "exclusive"} {
		t.Run(mode, func(t *testing.T) {
			other := own
			other.Port = 8
			hash := fakeNativeHash
			if mode != "same identity" {
				other.Serial = "OTHER0123456789"
			}
			if mode == "hash collision" || mode == "exclusive" {
				other.BusID = "2-1"
			}
			if mode == "hash collision" {
				hash = func(string, uint16, string) (uint32, error) { return 99, nil }
			}
			if mode == "zero hash" {
				hash = func(string, uint16, string) (uint32, error) { return 0, nil }
			}
			f := &fakeNativeTransport{items: []nativeOwnership{other}}
			a, err := attachNativeTransportWithHasher(context.Background(), own.Host, own.Service, own.BusID, own.Serial, f, mode == "exclusive", hash)
			if err == nil || a == nil {
				t.Fatal("unsafe baseline accepted")
			}
			if err = a.Close(context.Background()); err != nil || !f.closed {
				t.Fatal("refused attachment did not close its own handle:", err)
			}
			forbidNativeMutation(t, f.calls)
			if len(f.items) != 1 || f.items[0] != other {
				t.Fatal("refused attachment changed existing device")
			}
		})
	}
}

func TestNativeRuntimeRejectsChangedPortDuplicateAndHashCollision(t *testing.T) {
	for _, mode := range []string{"moved", "duplicate", "collision"} {
		t.Run(mode, func(t *testing.T) {
			f := &fakeNativeTransport{}
			a, err := attachFakeNative(fakeOwnedNative(), f)
			if err != nil {
				t.Fatal(err)
			}
			switch mode {
			case "moved":
				f.items[0].Port++
			case "duplicate":
				other := f.items[0]
				other.Port++
				f.items = append(f.items, other)
			case "collision":
				other := f.items[0]
				other.Port++
				other.Serial = "OTHER0123456789"
				other.BusID = "2-1"
				f.items = append(f.items, other)
				a.hashLocation = func(string, uint16, string) (uint32, error) { return a.locationHash, nil }
			}
			f.calls = nil
			if err = a.Alive(context.Background()); err == nil {
				t.Fatal("changed identity accepted by FRAME ownership check")
			}
			if err = a.Close(context.Background()); err == nil {
				t.Fatal("unsafe cleanup acknowledged")
			}
			forbidNativeMutation(t, f.calls)
		})
	}
}

func TestNativeCloseRechecksImmediatelyBeforeDetach(t *testing.T) {
	f := &fakeNativeTransport{}
	a, err := attachFakeNative(fakeOwnedNative(), f)
	if err != nil {
		t.Fatal(err)
	}
	f.beforeList = func(count int) {
		// Attach baseline=1; Close STOP check=2; Close list=3;
		// the final pre-DETACH list=4 observes the recycled port.
		if count == 4 {
			f.items[0].Serial = "OTHER0123456789"
		}
	}
	if err = a.Close(context.Background()); !errors.Is(err, errNativeOwnership) {
		t.Fatal("fresh reused port was not rejected:", err)
	}
	for _, code := range f.calls {
		if code == nativeIOCTLDetach {
			t.Fatal("detached a reused port")
		}
	}
}

func TestNativeLifecycleFinishesOwnPendingRequestBeforeRelease(t *testing.T) {
	var order []string
	a := &nativeAttachment{transport: &fakeNativeTransport{idleCheck: func() error { order = append(order, "idle"); return nil }}}
	if err := finishNativeLifecycle(func() error { order = append(order, "release"); return nil }, a); err != nil {
		t.Fatal(err)
	}
	if len(order) != 2 || order[0] != "idle" || order[1] != "release" {
		t.Fatal("lifecycle released before native completion:", order)
	}
	called := false
	a.transport = &fakeNativeTransport{idleCheck: func() error { return errors.New("unsettled") }}
	if err := finishNativeLifecycle(func() error { called = true; return nil }, a); err == nil || called {
		t.Fatal("unsettled request released topology ownership")
	}
}
