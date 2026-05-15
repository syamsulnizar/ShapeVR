using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Piece bentuk solid (Circle, Cube, Donut, Diamond, dll) yang dipegang pemain
/// dan dimasukkan ke ghost lubang siluet di Board.
///
/// State `IsShaped` di-replicate via NetworkVariable, jadi semua client melihat
/// progress yang sama.
///
/// VISUAL GHOST LIFECYCLE:
///   - Default: ghost invisible (MeshRenderer disabled).
///   - Saat piece di-hover oleh tangan/snap zone: ghost ON kalau belum shaped
///     (preview "kamu bisa snap di sini").
///   - Saat piece di-unhover: ghost OFF lagi.
///   - Saat snapped (IsShaped = true): ghost OFF (sudah ke-isi).
///   - Saat unsnap (IsShaped = false): ghost kembali OFF (default).
///   - Hover state hanya replicate saat belum shaped, untuk menghindari ghost
///     "berkedip" saat hover di piece yang sudah shaped.
///
/// SETUP:
///   - GameObject ini WAJIB punya NetworkObject component.
///   - Karena scene-placed, NetworkObject akan auto-spawn saat NGO load scene
///     (host wajib pakai NetworkSceneManager.LoadScene).
///   - Hubungkan UnityEvent SnapInteractor:
///       When Hover()    -> ShapeObject.OnHover()
///       When Unhover()  -> ShapeObject.OnUnhover()
///       When Select()   -> ShapeObject.Shape(true)   [kept]
///       When Unselect() -> ShapeObject.Shape(false)  [kept]
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class ShapeObject : NetworkBehaviour
{
    /// <summary>State authoritative — hanya server yang menulis.</summary>
    public NetworkVariable<bool> IsShaped = new NetworkVariable<bool>(
        false,
        readPerm: NetworkVariableReadPermission.Everyone,
        writePerm: NetworkVariableWritePermission.Server);

    /// <summary>
    /// Hover state per piece. Direplicate supaya semua pemain lihat ghost
    /// muncul ketika player A hover, bukan hanya di sisi A.
    /// Server-authoritative (client request via ServerRpc).
    /// </summary>
    public NetworkVariable<bool> IsHovered = new NetworkVariable<bool>(
        false,
        readPerm: NetworkVariableReadPermission.Everyone,
        writePerm: NetworkVariableWritePermission.Server);

    /// <summary>Backwards-compat: kode lama yang baca `isShaped` tetap jalan.</summary>
    public bool isShaped => IsShaped.Value;

    [Header("Visual / SFX")]
    [Tooltip("MeshRenderer ghost di Board — di-enable saat hover (preview), disable saat snapped.")]
    [SerializeField] private MeshRenderer silhouetteRenderer;
    [Tooltip("AudioSource untuk SFX snap. Kosongkan jika tidak perlu.")]
    [SerializeField] private AudioSource snapSfx;

    private GameManager _gameManager;

    public override void OnNetworkSpawn()
    {
        IsShaped.OnValueChanged += HandleShapedChanged;
        IsHovered.OnValueChanged += HandleHoverChanged;

        // Apply state awal (untuk client yang join mid-game)
        ApplyVisual();

        if (IsServer)
        {
            _gameManager = FindFirstObjectByType<GameManager>();
        }
    }

    public override void OnNetworkDespawn()
    {
        IsShaped.OnValueChanged -= HandleShapedChanged;
        IsHovered.OnValueChanged -= HandleHoverChanged;
    }

    private void HandleShapedChanged(bool previous, bool current)
    {
        ApplyVisual();

        // SFX hanya saat transisi ke shaped (avoid play saat sinkron awal client baru join).
        if (current && !previous && snapSfx != null && _hasStarted)
        {
            snapSfx.Play();
        }

        // Cek kemenangan hanya di server, hanya saat berubah jadi shaped.
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

        // Logic: ghost VISIBLE hanya saat hovered DAN belum shaped.
        bool shouldShow = IsHovered.Value && !IsShaped.Value;
        silhouetteRenderer.enabled = shouldShow;
    }

    private bool _hasStarted = false;
    private void Start()
    {
        _hasStarted = true;
        // Pastikan default ghost invisible saat scene start (sebelum spawn pun).
        if (silhouetteRenderer != null)
            silhouetteRenderer.enabled = false;
    }

    // ============================================================
    // PUBLIC API — dipanggil dari UnityEvent SnapInteractor
    // ============================================================

    /// <summary>UnityEvent: When Select() / When Unselect()</summary>
    public void Shape(bool shape)
    {
        if (!IsSpawned) return;

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
