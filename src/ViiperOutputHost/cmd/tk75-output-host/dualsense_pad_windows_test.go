//go:build windows && amd64

package main

import (
	"bytes"
	"testing"
)

// Pure descriptor/report checks; never opens a driver or starts USB transport.
func TestDualSensePadsHaveIndependentIdentityAndNeutralReports(t *testing.T) {
	one, err := newDualSensePad()
	if err != nil {
		t.Fatal(err)
	}
	two, err := newDualSensePad()
	if err != nil {
		t.Fatal(err)
	}
	if one.GetDescriptor().Device.ISerialNumber != 3 || two.GetDescriptor().Device.ISerialNumber != 3 ||
		!validNativeSerial(one.GetDescriptor().Strings[3]) || one.GetDescriptor().Strings[3] == two.GetDescriptor().Strings[3] {
		t.Fatal("devices need separate valid USB serials before enumeration")
	}
	for _, pad := range []*dualSensePad{one, two} {
		for i := 0; i < 10; i++ {
			if !neutralDualSenseReport(pad.HandleTransfer(4, 1, nil)) {
				t.Fatal("startup is not gameplay neutral")
			}
		}
	}
	first, ok := one.HandleControl(0xA1, 1, 0x0309, 3, 20, nil)
	if !ok || len(first) != 20 {
		t.Fatalf("pairing report %x, %v", first, ok)
	}
	second, ok := two.HandleControl(0xA1, 1, 0x0309, 3, 20, nil)
	if !ok || len(second) != 20 || bytes.Equal(first[1:7], second[1:7]) {
		t.Fatal("devices share their pairing identity")
	}
	if first[6]&3 != 2 || second[6]&3 != 2 {
		t.Fatal("identity is not a local unicast address")
	}
	if err := one.setPacket(packet{Buttons: 0x1000, LT: 255, LX: 12000}); err != nil {
		t.Fatal(err)
	}
	if neutralDualSenseReport(one.HandleTransfer(4, 1, nil)) {
		t.Fatal("active report stayed neutral")
	}
	if !neutralDualSenseReport(two.HandleTransfer(4, 1, nil)) {
		t.Fatal("another pad's input changed")
	}
	if err := one.setPacket(packet{}); err != nil {
		t.Fatal(err)
	}
	if !neutralDualSenseReport(one.HandleTransfer(4, 1, nil)) {
		t.Fatal("neutral reset failed")
	}
}
