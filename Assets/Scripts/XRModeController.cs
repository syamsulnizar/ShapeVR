using System.Collections;
using UnityEngine;

/// <summary>
/// Set XR mode per scene: VR (see skybox / virtual environment) or
/// Passthrough (see the real world via OVR Insight Passthrough).
///
/// PROBLEM SOLVED:
///   OVRManager is a DontDestroyOnLoad singleton. Setting passthrough in the
///   OVRManager Inspector of a new scene is never applied because the singleton
///   from the first scene persists. Solution: force toggle at runtime.
///
/// HOW TO USE:
///   - Attach 1 XRModeController GameObject per scene.
///   - Set `mode`:
///       Passthrough -> enable passthrough, see real world
///       VR          -> disable passthrough, see skybox
///   - Drag OVRPassthroughLayer + CenterEyeAnchor Camera (to toggle clear flag).
/// </summary>
public class XRModeController : MonoBehaviour
{
    public enum Mode { Passthrough, VR }

    [Tooltip("XR mode for this scene.")]
    [SerializeField] private Mode mode = Mode.Passthrough;

    [Header("Refs")]
    [Tooltip("OVRPassthroughLayer in the scene (only needed for Passthrough mode).")]
    [SerializeField] private OVRPassthroughLayer passthroughLayer;
    [Tooltip("CenterEyeAnchor Camera. ClearFlag/background will be set according to the mode.")]
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
        // Wait for OVRManager.instance to exist
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
        // 1. Enable insight passthrough in OVRManager
        if (!OVRManager.instance.isInsightPassthroughEnabled)
        {
            OVRManager.instance.isInsightPassthroughEnabled = true;
            Log("OVRManager.isInsightPassthroughEnabled = true");
        }

        // 2. Set Camera ClearFlag to SolidColor + transparent (alpha 0)
        if (centerEyeCamera != null)
        {
            centerEyeCamera.clearFlags = CameraClearFlags.SolidColor;
            centerEyeCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            Log("Camera ClearFlag=SolidColor, bg=(0,0,0,0)");
        }

        // 3. Wait until OS-level passthrough is initialized
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

        // 3. Hide PassthroughLayer if it exists (just in case)
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
