using UnityEngine;
using Unity.Netcode;

public class HostPlacementController : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private ColocationManager colocationManager;
    [SerializeField] private OVRHand rightHand;
    [SerializeField] private Transform rayOrigin;
    [SerializeField] private GameObject rayVisual;
    [SerializeField] private GameObject placementUIRoot;
    [SerializeField] private GameObject tablePreview;

    [Header("Placement")]
    [SerializeField] private float floorY = 0f;
    [SerializeField] private float maxRayDistance = 10f;

    [Header("Pinch")]
    [SerializeField] private float pinchCooldownAtStart = 1f;

    [Header("Debug")]
    [SerializeField] private bool verboseLogging = true;

    private bool _placementActive = false;
    private bool _placementLocked = false;
    private bool _wasPinchingLastFrame = false;
    private float _placementStartTime;
    private Vector3 _currentHitPoint;
    private bool _hasValidHit = false;
    private float _lastDiagLogTime = 0f;
    private bool _isHostThisSession = false;

    private void Awake() { HideAllPlacementVisuals(); }
    private void Start() { HideAllPlacementVisuals(); }

    private void HideAllPlacementVisuals()
    {
        if (placementUIRoot != null) placementUIRoot.SetActive(false);
        if (rayVisual != null) rayVisual.SetActive(false);
        if (tablePreview != null) tablePreview.SetActive(false);
    }

    private void OnEnable()
    {
        if (colocationManager != null)
        {
            colocationManager.OnAnchorReady += HandleAnchorReady;
            colocationManager.OnTablePlaced += HandleTablePlaced;
        }
    }

    private void OnDisable()
    {
        if (colocationManager != null)
        {
            colocationManager.OnAnchorReady -= HandleAnchorReady;
            colocationManager.OnTablePlaced -= HandleTablePlaced;
        }
    }

    private void TryAutoFindRightHand()
    {
        if (rightHand != null) return;
        var allHands = FindObjectsByType<OVRHand>(FindObjectsSortMode.None);
        foreach (var h in allHands)
        {
            if (h == null) continue;
            try
            {
                if (h.GetHand() == OVRPlugin.Hand.HandRight)
                {
                    rightHand = h;
                    return;
                }
            }
            catch { }
        }
    }

    private void HandleAnchorReady(Transform anchorTransform)
    {
        _isHostThisSession = NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
        if (!_isHostThisSession)
        {
            Debug.Log("[HostPlacementController] CLIENT — hiding visuals.");
            HideAllPlacementVisuals();
            return;
        }
        Debug.Log("[HostPlacementController] HOST — placement mode.");
        StartPlacementMode();
    }

    private void HandleTablePlaced()
    {
        Debug.Log("[HostPlacementController] Table placed — hiding visuals.");
        StopPlacementMode();
    }

    private void StartPlacementMode()
    {
        TryAutoFindRightHand();
        _placementActive = true;
        _placementLocked = false;
        _wasPinchingLastFrame = false;
        _placementStartTime = Time.time;
        if (placementUIRoot != null) placementUIRoot.SetActive(true);
        if (rayVisual != null) rayVisual.SetActive(true);
    }

    private void StopPlacementMode()
    {
        _placementActive = false;
        HideAllPlacementVisuals();
    }

    private void Update()
    {
        if (!_placementActive) return;
        if (!_isHostThisSession) return;
        if (rayOrigin == null) return;

        if (rightHand == null || !rightHand.IsTracked)
        {
            _wasPinchingLastFrame = false;
            TryAutoFindRightHand();
            if (rightHand == null || !rightHand.IsTracked)
            {
                if (tablePreview != null) tablePreview.SetActive(false);
                return;
            }
        }

        if (verboseLogging && Time.time - _lastDiagLogTime > 2f)
        {
            _lastDiagLogTime = Time.time;
            Debug.Log("[HostPlacementController] rayFwd=" + rayOrigin.forward);
        }

        Vector3 rOrigin = rayOrigin.position;
        Vector3 rDir = rayOrigin.forward;
        _hasValidHit = TryRaycastToFloor(rOrigin, rDir, out _currentHitPoint);

        if (tablePreview != null)
        {
            tablePreview.SetActive(_hasValidHit);
            if (_hasValidHit)
            {
                _currentHitPoint.y = floorY;
                tablePreview.transform.position = _currentHitPoint;
                tablePreview.transform.rotation = Quaternion.identity;
            }
        }

        bool isPinchingNow = rightHand.GetFingerIsPinching(OVRHand.HandFinger.Index);
        bool risingEdge = isPinchingNow && !_wasPinchingLastFrame;
        _wasPinchingLastFrame = isPinchingNow;

        bool cooldownPassed = (Time.time - _placementStartTime) >= pinchCooldownAtStart;

        if (risingEdge && cooldownPassed && !_placementLocked && _hasValidHit)
        {
            _placementLocked = true;
            Debug.Log("[HostPlacementController] Pinch! Place at " + _currentHitPoint);
            if (colocationManager != null)
            {
                //colocationManager.ConfirmTablePlacement(_currentHitPoint);
            }
        }
    }

    private bool TryRaycastToFloor(Vector3 origin, Vector3 direction, out Vector3 hitPoint)
    {
        hitPoint = Vector3.zero;
        if (direction.y >= -0.001f)
        {
            Vector3 horiz = direction;
            horiz.y = 0;
            if (horiz.sqrMagnitude < 0.001f) return false;
            horiz.Normalize();
            hitPoint = new Vector3(origin.x + horiz.x * 2f, floorY, origin.z + horiz.z * 2f);
            return true;
        }
        float t = (floorY - origin.y) / direction.y;
        if (t < 0f || t > maxRayDistance) return false;
        hitPoint = origin + direction * t;
        return true;
    }
}
