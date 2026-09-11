//go:build windows && amd64

package main

import (
	"encoding/binary"
	"errors"
	"sync"

	"github.com/Alia5/VIIPER/device/dualsense"
	"github.com/Alia5/VIIPER/usb"
)

// This is a real DualSense USB device adapter, not an Xbox descriptor with a
// different name. Construction and state updates are in-memory operations.
// The separate owned-HID Windows observer implements startup, neutralization
// and removal checks; actual acceptance remains gated. Never substitute the
// existing Xbox XInput-slot observer or report generation for that evidence.
type dualSensePad struct {
	mu sync.Mutex
	*dualsense.DualSense
	lastReportStamp uint32
}

var _ usb.Device = (*dualSensePad)(nil)

func newDualSensePad() (*dualSensePad, error) {
	d, err := dualsense.New(nil)
	if err != nil {
		return nil, err
	}
	descriptor := d.GetDescriptor()
	if descriptor.Device.IDVendor != 0x054C || descriptor.Device.IDProduct != 0x0CE6 {
		return nil, errors.New("DualSense descriptor identity differs from pinned profile")
	}
	p := &dualSensePad{DualSense: d}
	if err = p.setPacket(packet{}); err != nil {
		return nil, err
	}
	return p, nil
}

func (p *dualSensePad) setPacket(value packet) error {
	_, err := p.setPacketAfter(value)
	return err
}
func (p *dualSensePad) setPacketAfter(value packet) (uint32, error) {
	encoded, err := encodeDualSense(value)
	if err != nil {
		return 0, err
	}
	state := new(dualsense.InputState)
	if err = state.UnmarshalBinary(encoded[:]); err != nil {
		return 0, err
	}
	p.mu.Lock()
	defer p.mu.Unlock()
	p.DualSense.UpdateInputState(state)
	return p.lastReportStamp, nil
}

// Upstream increments its report sequence outside its input-state lock. Route
// control and interrupt transfers through one adapter lock for deterministic
// report construction. The device is private; callers never mutate a retained
// InputState pointer after UpdateInputState.
func (p *dualSensePad) HandleTransfer(ep, dir uint32, out []byte) []byte {
	p.mu.Lock()
	defer p.mu.Unlock()
	report := p.DualSense.HandleTransfer(ep, dir, out)
	if ep == 4 && dir == 1 && len(report) == 64 && report[0] == 1 {
		p.lastReportStamp = binary.LittleEndian.Uint32(report[28:32])
	}
	return report
}
func (p *dualSensePad) HandleControl(bmRequestType, bRequest uint8, wValue, wIndex, wLength uint16, data []byte) ([]byte, bool) {
	p.mu.Lock()
	defer p.mu.Unlock()
	report, ok := p.DualSense.HandleControl(bmRequestType, bRequest, wValue, wIndex, wLength, data)
	if ok && bmRequestType == 0xA1 && bRequest == 1 && wValue == 0x0101 && len(report) == 64 {
		p.lastReportStamp = binary.LittleEndian.Uint32(report[28:32])
	}
	return report, ok
}

func newDualSenseBackend() outputBackend {
	// Implementation exists, but no live acceptance has been completed.
	return &unavailableOutput{reason: dualSenseAcceptanceRequired}
}
