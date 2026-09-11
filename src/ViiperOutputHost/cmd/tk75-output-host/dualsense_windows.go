//go:build windows && amd64

package main

import (
	"crypto/rand"
	"encoding/binary"
	"errors"
	"fmt"
	"sync"

	"github.com/Alia5/VIIPER/device"
	"github.com/Alia5/VIIPER/device/dualsense"
	"github.com/Alia5/VIIPER/usb"
)

// This is a real DualSense USB device adapter, not an Xbox descriptor with a
// different name. Construction and state updates are in-memory operations.
// The separate owned-HID Windows observer implements startup, neutralization
// and removal checks. Never substitute the
// existing Xbox XInput-slot observer or report generation for that evidence.
type dualSensePad struct {
	mu sync.Mutex
	*dualsense.DualSense
	lastReportStamp uint32
	lastPacket      packet
	pacer           reportPacer
}

var _ usb.Device = (*dualSensePad)(nil)

func newDualSensePad() (*dualSensePad, error) {
	serial, err := newNativeSerial()
	if err != nil {
		return nil, err
	}
	return newDualSensePadWithSerial(serial)
}

func newDualSensePadWithSerial(serial string) (*dualSensePad, error) {
	if !validNativeSerial(serial) {
		return nil, errors.New("invalid DualSense USB serial")
	}
	// Applications can identify a DualSense using its pairing feature report,
	// not only the USB serial supplied by usbip-win2. Give every device its own
	// local unicast MAC and feature serial instead of cloning upstream defaults.
	var identity [14]byte
	if _, err := rand.Read(identity[:]); err != nil {
		return nil, err
	}
	identity[0] = (identity[0] & 0xFC) | 0x02
	options := &device.CreateOptions{DeviceSpecific: fmt.Sprintf(
		`{"mac_address":"%02X:%02X:%02X:%02X:%02X:%02X","serial_number":"A%X"}`,
		identity[0], identity[1], identity[2], identity[3], identity[4], identity[5], identity[6:])}
	d, err := dualsense.New(options)
	if err != nil {
		return nil, err
	}
	descriptor := d.GetDescriptor()
	if descriptor.Device.IDVendor != 0x054C || descriptor.Device.IDProduct != 0x0CE6 {
		return nil, errors.New("DualSense descriptor identity differs from pinned profile")
	}
	// Advertise the same serial as our native attachment from the first device
	// descriptor. Do not depend on a driver's late descriptor patch for PnP IDs.
	strings := make(map[uint8]string, len(descriptor.Strings)+1)
	for key, value := range descriptor.Strings {
		strings[key] = value
	}
	strings[3] = serial
	descriptor.Strings = strings
	descriptor.Device.ISerialNumber = 3
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
	changed := p.lastPacket != value
	p.DualSense.UpdateInputState(state)
	p.lastPacket = value
	stamp := p.lastReportStamp
	p.mu.Unlock()
	if changed {
		p.pacer.changed()
	}
	return stamp, nil
}

// Upstream increments its report sequence outside its input-state lock. Route
// control and interrupt transfers through one adapter lock for deterministic
// report construction. The device is private; callers never mutate a retained
// InputState pointer after UpdateInputState.
func (p *dualSensePad) HandleTransfer(ep, dir uint32, out []byte) []byte {
	if ep == 4 && dir == 1 {
		p.pacer.wait()
	}
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
	if !dualSenseRuntimeAcceptanceRecorded {
		return &unavailableOutput{reason: dualSenseAcceptanceRequired}
	}
	return &dualSenseUSBBackend{cleanupDone: make(chan struct{})}
}
