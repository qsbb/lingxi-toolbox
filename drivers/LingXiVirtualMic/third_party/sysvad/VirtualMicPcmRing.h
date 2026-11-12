#pragma once

#include <ntddk.h>

// Fixed-format bridge between the virtual render stream and capture stream.
// Both endpoints are constrained to 48 kHz, stereo, signed PCM16 (4 bytes/frame).
void LingXiPcmRingInitialize();
void LingXiPcmRingReset();
void LingXiPcmRingWrite(const BYTE* source, ULONG bytes);
void LingXiPcmRingRead(BYTE* destination, ULONG bytes);
