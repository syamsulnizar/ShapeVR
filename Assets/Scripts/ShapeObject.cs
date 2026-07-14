using UnityEngine;
using Unity.Netcode;
using Oculus.Interaction;

/// <summary>
/// A solid-shaped piece (Circle, Cube, Donut, Diamond, etc.) held by the player
/// and inserted into the silhouette-hole ghost on the Board.
///
/// The `IsShaped` state is replicated via NetworkVariable, so all clients see
/// the same progress.
///
/// VISUAL GHOST LIFECYCLE:
///   - Default: ghost is invisible (MeshRenderer disabled).
///   - When the piece is hovered by a hand/snap zone: ghost turns ON if it has not
///     been shaped yet (preview: "you can snap here").
///   - When the piece is unhovered: ghost turns OFF again.
///   - When snapped (IsShaped = true): ghost turns OFF (already filled).
///   - When unsnapped (IsShaped = false): ghost returns to OFF (default).
///   - Hover state is only replicated while not shaped, to prevent the ghost from
///     "flickering" when hovering over a piece that has already been shaped.
///
/// SETUP:
///   - This GameObject MUST have a NetworkObject component.
///   - Because it is scene-placed, the NetworkObject will auto-spawn when NGO loads
///     the scene (the host must use NetworkSceneManager.LoadScene).
///   - Connect the SnapInteractor UnityEvent:
///       When Hover()    -> ShapeObject.OnHover()
///       When Unhover()  -> ShapeObject.OnUnhover()
///       When Select()   -> ShapeObject.Shape(true)   [kept]
///       When Unselect() -> ShapeObject.Shape(false)  [kept]
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class ShapeObject : NetworkBehaviour
{
    /// <summary>State authoritative — server-authoritative only.</summary>
    public NetworkVariable<bool> IsShaped = new NetworkVariable<bool>(
        false,
        readPerm: NetworkVariableReadPermission.Everyone,
        writePerm: NetworkVariableWritePermission.Server);

    /// <summary>
    /// Hover state per piece
    /// Server-authoritative 
    /// </summary>
    public NetworkVariable<bool> IsHovered = new NetworkVariable<bool>(
        false,
        readPerm: NetworkVariableReadPermission.Everyone,
        writePerm: NetworkVariableWritePermission.Server);

    public bool isShaped => IsShaped.Value;

    [Header("Visual / SFX")]
    [Tooltip("MeshRenderer ghost on the Board — enabled on hover (preview), disabled when snapped.")]
    [SerializeField] private MeshRenderer silhouetteRenderer;
    [Tooltip("AudioSource for snap SFX. Leave empty if not needed.")]
    [SerializeField] private AudioSource snapSfx;
    [Tooltip("Short delay to allow the SnapInteractor time to change state after the shape is released.")]
    [SerializeField] private float wrongReleaseCheckDelay = 0.2f;
    [Tooltip("Additional area to check overlap with the snap board when the shape is released.")]
    [SerializeField] private float wrongSnapOverlapPadding = 0.03f;

    private GameManager _gameManager;
    private Grabbable _grabbable;
    private Collider _shapeCollider;
    private Coroutine _wrongReleaseRoutine;

    public override void OnNetworkSpawn()
    {
        IsShaped.OnValueChanged += HandleShapedChanged;
        IsHovered.OnValueChanged += HandleHoverChanged;

        // Apply initial state (for clients joining mid-game)
        ApplyVisual();

        if (IsServer)
        {
            _gameManager = FindFirstObjectByType<GameManager>();
        }

        SubscribeGrabRelease();
    }

    public override void OnNetworkDespawn()
    {
        IsShaped.OnValueChanged -= HandleShapedChanged;
        IsHovered.OnValueChanged -= HandleHoverChanged;
        UnsubscribeGrabRelease();
    }

    private void OnDisable()
    {
        UnsubscribeGrabRelease();
    }

    private void OnEnable()
    {
        SubscribeGrabRelease();
    }

    private void HandleShapedChanged(bool previous, bool current)
    {
        ApplyVisual();

        if (current && !previous && snapSfx != null && _hasStarted)
        {
            snapSfx.Play();
        }

        if (IsServer && current && _gameManager != null)
        {
            _gameManager.CheckCondition();
        }
    }

    private void HandleHoverChanged(bool previous, bool current)
    {
        ApplyVisual();
    }

    private void ApplyVisual()
    {
        if (silhouetteRenderer == null) return;

        bool shouldShow = IsHovered.Value && !IsShaped.Value;
        silhouetteRenderer.enabled = shouldShow;
    }

    private bool _hasStarted = false;
    private void Start()
    {
        _hasStarted = true;
        if (silhouetteRenderer != null)
            silhouetteRenderer.enabled = false;

        _shapeCollider = GetComponent<Collider>();
        SubscribeGrabRelease();
    }

    // ============================================================
    // PUBLIC API
    // ============================================================

    /// <summary>UnityEvent: When Select() / When Unselect()</summary>
    public void Shape(bool shape)
    {
        if (!IsSpawned) return;

        if (shape)
        {
            if (!IsShaped.Value && PlayerDataSaver.Instance != null)
            {
                PlayerDataSaver.Instance.IncrementCorrectAnswers();
            }
        }
        else
        {
            if (IsShaped.Value && PlayerDataSaver.Instance != null)
            {
                PlayerDataSaver.Instance.DecrementCorrectAnswers();
            }
        }

        if (IsServer)
        {
            IsShaped.Value = shape;
        }
        else
        {
            RequestShapeServerRpc(shape);
        }
    }

    /// <summary>UnityEvent: When Hover()</summary>
    public void OnHover()
    {
        SetHovered(true);
    }

    /// <summary>UnityEvent: When Unhover()</summary>
    public void OnUnhover()
    {
        SetHovered(false);
    }

    private void SetHovered(bool hovered)
    {
        if (!IsSpawned) return;

        if (IsServer)
        {
            IsHovered.Value = hovered;
        }
        else
        {
            RequestHoverServerRpc(hovered);
        }
    }

    public void HideObject()
    {
        gameObject.GetComponent<MeshRenderer>().enabled = false;
        gameObject.GetComponent<Collider>().enabled = false;
    }

    public void ShowObject()
    {
        gameObject.GetComponent<MeshRenderer>().enabled = true;
        gameObject.GetComponent<Collider>().enabled = true;
    }

    private void SubscribeGrabRelease()
    {
        if (_grabbable != null) return;

        _grabbable = GetComponent<Grabbable>();
        if (_grabbable != null)
            _grabbable.WhenPointerEventRaised += HandleGrabPointerEvent;
    }

    private void UnsubscribeGrabRelease()
    {
        if (_grabbable == null) return;

        _grabbable.WhenPointerEventRaised -= HandleGrabPointerEvent;
        _grabbable = null;
    }

    private void HandleGrabPointerEvent(PointerEvent evt)
    {
        if (evt.Type != PointerEventType.Unselect) return;

        // Error count is local-only. In network play, only the current owner should count it.
        if (IsSpawned && !IsOwner) return;

        if (_wrongReleaseRoutine != null)
            StopCoroutine(_wrongReleaseRoutine);

        _wrongReleaseRoutine = StartCoroutine(CheckWrongReleaseAfterDelay());
    }

    private System.Collections.IEnumerator CheckWrongReleaseAfterDelay()
    {
        yield return new WaitForSeconds(wrongReleaseCheckDelay);

        _wrongReleaseRoutine = null;

        if (IsShaped.Value) yield break;
        
        Collider wrongCollider = GetWrongBoardSnapCollider();
        if (wrongCollider == null) yield break;

        ShapeErrorLogger logger = ShapeErrorLogger.Instance;
        if (logger == null)
            logger = FindFirstObjectByType<ShapeErrorLogger>();

        if (logger != null)
        {
            string shapeName = gameObject.name;
            string targetName = wrongCollider.gameObject.name.Replace(" Snap", "").Replace(" snap", "");
            logger.ReportWrongReleaseDetailed(shapeName, targetName);
        }
    }

    private Collider GetWrongBoardSnapCollider()
    {
        if (_shapeCollider == null)
            _shapeCollider = GetComponent<Collider>();

        if (_shapeCollider == null)
            return null;

        Bounds bounds = _shapeCollider.bounds;
        Vector3 halfExtents = bounds.extents + Vector3.one * wrongSnapOverlapPadding;
        Collider[] hits = Physics.OverlapBox(bounds.center, halfExtents, Quaternion.identity, ~0, QueryTriggerInteraction.Collide);

        foreach (Collider hit in hits)
        {
            if (hit == null || hit == _shapeCollider) continue;
            if (hit.transform == transform || hit.transform.IsChildOf(transform)) continue;
            if (!IsBoardSnapCollider(hit)) continue;
            if (IsCorrectSnapCollider(hit)) continue;

            return hit;
        }

        return null;
    }

    private bool IsBoardSnapCollider(Collider hit)
    {
        string objectName = hit.gameObject.name;
        if (!objectName.EndsWith(" Snap", System.StringComparison.OrdinalIgnoreCase))
            return false;

        Transform parent = hit.transform.parent;
        return parent != null && parent.name == "Board";
    }

    private bool IsCorrectSnapCollider(Collider hit)
    {
        string expectedSnapName = $"{gameObject.name} Snap";
        return string.Equals(hit.gameObject.name, expectedSnapName, System.StringComparison.OrdinalIgnoreCase);
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestShapeServerRpc(bool shape, ServerRpcParams rpcParams = default)
    {
        IsShaped.Value = shape;
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestHoverServerRpc(bool hovered, ServerRpcParams rpcParams = default)
    {
        IsHovered.Value = hovered;
    }
}
