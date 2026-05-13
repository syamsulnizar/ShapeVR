using System.Collections;
using UnityEngine;

/// <summary>
/// Atur mode XR per scene: VR (lihat skybox / virtual environment) atau
/// Passthrough (lihat dunia nyata via OVR Insight Passthrough).
///
/// MASALAH YANG DI-SOLVE:
///   OVRManager adalah singleton DontDestroyOnLoad. Setting passthrough di
///   Inspector OVRManager scene baru tidak pernah di-apply karena singleton
///   dari scene pertama yang persist. Solusinya: paksa toggle di runtime.
///
/// CARA PAKAI:
///   - Attach 1 GameObject XRModeController per scene.
///   - Set `mode`:
///       Passthrough -> enable passthrough, see real world
///       VR          -> disable passthrough, see skybox
///   - Drag OVRPassthroughLayer + CenterEyeAnchor Camera (untuk toggle clear flag).
/// </summary>
public class XRModeController : MonoBehaviour
{
    public enum Mode { Passthrough, VR }

    [Tooltip("Mode XR untuk scene ini.")]
    [SerializeField] private Mode mode = Mode.Passthrough;

    [Header("Refs")]
    [Tooltip("OVRPassthroughLayer di scene (hanya dibutuhkan untuk Passthrough mode).")]
    [SerializeField] private OVRPassthroughLayer passthroughLayer;
    [Tooltip("CenterEyeAnchor Camera. Akan di-set ClearFlag/background sesuai mode.")]
    [SerializeField] private Camera centerEyeCamera;

    [Header("Debug")]
    [SerializeField] private bool verboseLogging = true;

    private void OnEnable()
    {
        OVRManager.InputFocusAcquired += HandleInputFocusAcquired;
        StartCoroutine(ApplyModeCoroutine());
    }

    private void OnDisable()
    {
        OVRManager.InputFocusAcquired -= HandleInputFocusAcquired;
    }

    private IEnumerator ApplyModeCoroutine()
    {
        // Tunggu OVRManager.instance ada
        int waitFrames = 0;
        while (OVRManager.instance == null && waitFrames < 60)
        {
            yield return null;
            waitFrames++;
        }

        if (OVRManager.instance == null)
        {
            Debug.LogError("[XRModeController] OVRManager.instance null after wait.");
            yield break;
        }

        if (mode == Mode.Passthrough)
        {
            yield return ApplyPassthroughMode();
        }
        else
        {
            ApplyVRMode();
        }
    }

    private IEnumerator ApplyPassthroughMode()
    {
        // 1. Enable insight passthrough di OVRManager
        if (!OVRManager.instance.isInsightPassthroughEnabled)
        {
            OVRManager.instance.isInsightPassthroughEnabled = true;
            Log("OVRManager.isInsightPassthroughEnabled = true");
        }

        // 2. Set Camera ClearFlag SolidColor + transparent (alpha 0)
        if (centerEyeCamera != null)
        {
            centerEyeCamera.clearFlags = CameraClearFlags.SolidColor;
            centerEyeCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            Log("Camera ClearFlag=SolidColor, bg=(0,0,0,0)");
        }

        // 3. Tunggu sampai passthrough OS-level initialized
        int waitInit = 0;
        while (!OVRManager.IsInsightPassthroughInitialized() && waitInit < 120)
        {
            yield return null;
            waitInit++;
        }

        if (!OVRManager.IsInsightPassthroughInitialized())
        {
            Debug.LogWarning("[XRModeController] Passthrough init timeout.");
        }
        else
        {
            Log("Insight Passthrough Initialized OK.");
        }

        // 4. Enable PassthroughLayer
        if (passthroughLayer != null)
        {
            if (!passthroughLayer.enabled) passthroughLayer.enabled = true;
            passthroughLayer.hidden = false;
            Log("OVRPassthroughLayer enabled+visible.");
        }
    }

    private void ApplyVRMode()
    {
        // 1. Disable insight passthrough
        if (OVRManager.instance.isInsightPassthroughEnabled)
        {
            OVRManager.instance.isInsightPassthroughEnabled = false;
            Log("OVRManager.isInsightPassthroughEnabled = false");
        }

        // 2. Set Camera ClearFlag = Skybox (default VR behavior)
        if (centerEyeCamera != null)
        {
            centerEyeCamera.clearFlags = CameraClearFlags.Skybox;
            Log("Camera ClearFlag=Skybox.");
        }

        // 3. Hide PassthroughLayer kalau ada (jaga-jaga)
        if (passthroughLayer != null)
        {
            passthroughLayer.hidden = true;
        }
    }

    private void HandleInputFocusAcquired()
    {
        Log("InputFocusAcquired \u2014 re-applying XR mode.");
        StartCoroutine(ApplyModeCoroutine());
    }

    private void Log(string msg)
    {
        if (verboseLogging) Debug.Log("[XRModeController] " + msg);
    }
}
