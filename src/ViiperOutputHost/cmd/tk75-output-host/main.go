package main

import "os"

func main() {
	if len(os.Args) != 2 {
		os.Exit(64)
	}
	var factory func() outputBackend
	limits := normalLimits
	switch os.Args[1] {
	case "--output-host":
		factory = func() outputBackend { return newUSBBackend() }
	case "--xbox-probe-host":
		factory = func() outputBackend { return newXboxProbeBackend() }
		limits = probeLimits
	case "--xbox-runtime-host":
		factory = func() outputBackend { return newXboxRuntimeBackend() }
		// Keep immediate neutralization and separately bounded Windows removal.
		limits = probeLimits
	case "--dualsense-host":
		factory = func() outputBackend { return newDualSenseBackend() }
		limits = probeLimits
	case "--dualsense-probe-host":
		factory = func() outputBackend { return newDualSenseProbeBackend() }
		limits = probeLimits
	default:
		os.Exit(64)
	}
	os.Exit(runHost(os.Stdin, os.Stdout, factory, limits))
}
