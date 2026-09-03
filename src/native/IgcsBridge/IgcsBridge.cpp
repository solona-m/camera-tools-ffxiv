// IgcsBridge - the native export surface for CameraToolsXIV.
//
// ReShade add-ons (Marty McFly's MartysMods_ParallaxDOF.addon64, Otis_Inf's
// IgcsConnector.addon64) discover a camera tool by walking the loaded modules of the
// game process with EnumProcessModules and calling GetProcAddress for
// "IGCS_StartScreenshotSession". A managed Dalamud assembly has no PE export table, so
// it can never be found that way. This DLL is the smallest thing that can be: it
// exports the four IGCS entry points and forwards them to function pointers that the
// managed plugin registers at load time.
//
// The IGCS ABI is documented in IgcsConnector's CameraToolsConnector.h (BSD licensed,
// https://github.com/FransBouma/IgcsConnector). This is an independent implementation
// of that interface, not a copy of it.
//
// Threading: add-ons call the IGCS_* entry points from ReShade's present callback,
// i.e. the render thread, while the plugin registers and unregisters from the game
// thread. Dalamud plugins unload and reload constantly during development, so a
// dangling callback pointer being called from the render thread is a crash waiting to
// happen. The SRW lock below makes unregistration wait for in-flight calls to drain.

#include <windows.h>
#include <cstdint>

namespace
{
    // Mirrors the typedefs in IgcsConnector's CameraToolsConnector.h.
    //
    // Note the return type of startScreenshotSession: IGCS declares it as
    // ScreenshotSessionStartReturnCode, which is `uint8_t` only under IGCS32BIT and
    // plain `int` otherwise. We are x64-only, so it is a 4-byte int.
    struct IgcsCallbacks
    {
        int32_t(*startScreenshotSession)(uint8_t type);
        void (*moveCameraPanorama)(float stepAngle);
        void (*moveCameraMultishot)(float stepLeftRight, float stepUpDown, float fovDegrees, uint8_t fromStartPosition);
        void (*endScreenshotSession)();
    };

    // ScreenshotSessionStartReturnCode::Error_CameraFeatureNotAvailable
    constexpr int32_t kErrorCameraFeatureNotAvailable = 4;

    SRWLOCK g_lock = SRWLOCK_INIT;
    IgcsCallbacks g_callbacks = {};
    bool g_registered = false;
}

extern "C"
{
    // --- Called by the ReShade add-on -------------------------------------------------

    // type: 0 = horizontal panorama, 1 = multishot, 2 = debug grid.
    // Returns 0 on success, or 1..5 per ScreenshotSessionStartReturnCode.
    int32_t IGCS_StartScreenshotSession(uint8_t type)
    {
        AcquireSRWLockShared(&g_lock);
        const int32_t result = (g_registered && g_callbacks.startScreenshotSession != nullptr)
            ? g_callbacks.startScreenshotSession(type)
            : kErrorCameraFeatureNotAvailable;
        ReleaseSRWLockShared(&g_lock);
        return result;
    }

    // stepAngle in radians; positive rotates right.
    void IGCS_MoveCameraPanorama(float stepAngle)
    {
        AcquireSRWLockShared(&g_lock);
        if (g_registered && g_callbacks.moveCameraPanorama != nullptr)
        {
            g_callbacks.moveCameraPanorama(stepAngle);
        }
        ReleaseSRWLockShared(&g_lock);
    }

    // The entry point Parallax DoF and IGCS DoF drive the camera with. fovDegrees <= 0
    // means "leave the FoV alone". fromStartPosition is a C++ bool, i.e. one byte.
    void IGCS_MoveCameraMultishot(float stepLeftRight, float stepUpDown, float fovDegrees, bool fromStartPosition)
    {
        AcquireSRWLockShared(&g_lock);
        if (g_registered && g_callbacks.moveCameraMultishot != nullptr)
        {
            g_callbacks.moveCameraMultishot(stepLeftRight, stepUpDown, fovDegrees, fromStartPosition ? 1u : 0u);
        }
        ReleaseSRWLockShared(&g_lock);
    }

    void IGCS_EndScreenshotSession()
    {
        AcquireSRWLockShared(&g_lock);
        if (g_registered && g_callbacks.endScreenshotSession != nullptr)
        {
            g_callbacks.endScreenshotSession();
        }
        ReleaseSRWLockShared(&g_lock);
    }

    // --- Called by the managed plugin -------------------------------------------------

    // Installs the managed callbacks. Passing null clears them, same as Unregister.
    void IGCSBRIDGE_Register(const IgcsCallbacks* callbacks)
    {
        AcquireSRWLockExclusive(&g_lock);
        if (callbacks == nullptr)
        {
            g_callbacks = {};
            g_registered = false;
        }
        else
        {
            g_callbacks = *callbacks;
            g_registered = true;
        }
        ReleaseSRWLockExclusive(&g_lock);
    }

    // Blocks until any in-flight add-on call has returned, so the plugin can unload
    // without leaving the render thread holding a pointer into freed managed code.
    void IGCSBRIDGE_Unregister()
    {
        AcquireSRWLockExclusive(&g_lock);
        g_callbacks = {};
        g_registered = false;
        ReleaseSRWLockExclusive(&g_lock);
    }

    // Lets the plugin verify it loaded the DLL it expected.
    uint32_t IGCSBRIDGE_GetVersion()
    {
        return 1;
    }
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}
