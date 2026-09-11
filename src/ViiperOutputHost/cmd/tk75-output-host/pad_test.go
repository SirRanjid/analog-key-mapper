//go:build windows && amd64

package main

import (
	"bytes"
	"github.com/Alia5/VIIPER/device/xbox360"
	"github.com/Alia5/VIIPER/usbip"
	"testing"
)

// Calls only a Go object's report encoder; no USB server, driver or XInput.
func TestUpstreamFirstInputIsNeutralAndRepeatedZerosAreReturned(t *testing.T) {
	p, err := newTrackedPad()
	if err != nil {
		t.Fatal(err)
	}
	want := make([]byte, 20)
	want[1] = 20
	if got := p.HandleTransfer(1, usbip.DirIn, nil); !bytes.Equal(got, want) {
		t.Fatalf("first report % X", got)
	}
	select {
	case <-p.firstNeutral:
	default:
		t.Fatal("first neutral evidence missing")
	}
	p.UpdateInputState(xbox360.InputState{Buttons: 0x1000, LT: 255, LX: -32768, RY: 32767})
	got := p.HandleTransfer(1, usbip.DirIn, nil)
	if got[3] != 0x10 || got[4] != 255 || got[7] != 0x80 || got[12] != 255 || got[13] != 0x7f {
		t.Fatalf("golden active fields % X", got)
	}
	p.UpdateInputState(xbox360.InputState{})
	for i := 0; i < 10; i++ {
		if got := p.HandleTransfer(1, usbip.DirIn, nil); !bytes.Equal(got, want) {
			t.Fatalf("zero %d suppressed/changed: % X", i, got)
		}
	}
}
