#pragma once

// Build-time selection of the virtual camera identity.
// Build with /DSOFTCAM_VARIANT=1 or /DSOFTCAM_VARIANT=2 (e.g. `set CL=/DSOFTCAM_VARIANT=1`)
// so that two independent virtual webcams can coexist on one system.
// Each variant has its own CLSID, device name, mutex and shared memory.

#ifndef SOFTCAM_VARIANT
#define SOFTCAM_VARIANT 0
#endif

#if SOFTCAM_VARIANT == 1
  // {10F87A76-EC04-4014-8038-A94D5821E192}
  #define SOFTCAM_DEFINE_CLSID(name) DEFINE_GUID(name, 0x10f87a76, 0xec04, 0x4014, 0x80, 0x38, 0xa9, 0x4d, 0x58, 0x21, 0xe1, 0x92)
  #define SOFTCAM_NAME_A "Golf Cam 1"
  #define SOFTCAM_NAME_W L"Golf Cam 1"
#elif SOFTCAM_VARIANT == 2
  // {8CC08C60-EDDD-4522-87D2-BE41E5125E3E}
  #define SOFTCAM_DEFINE_CLSID(name) DEFINE_GUID(name, 0x8cc08c60, 0xeddd, 0x4522, 0x87, 0xd2, 0xbe, 0x41, 0xe5, 0x12, 0x5e, 0x3e)
  #define SOFTCAM_NAME_A "Golf Cam 2"
  #define SOFTCAM_NAME_W L"Golf Cam 2"
#else
  // Original softcam identity {AEF3B972-5FA5-4647-9571-358EB472BC9E}
  #define SOFTCAM_DEFINE_CLSID(name) DEFINE_GUID(name, 0xaef3b972, 0x5fa5, 0x4647, 0x95, 0x71, 0x35, 0x8e, 0xb4, 0x72, 0xbc, 0x9e)
  #define SOFTCAM_NAME_A "DirectShow Softcam"
  #define SOFTCAM_NAME_W L"DirectShow Softcam"
#endif
