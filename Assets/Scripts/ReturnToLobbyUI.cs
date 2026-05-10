using UnityEngine;
using TMPro;

/// <summary>
/// UI panel yang menampilkan countdown kembali ke lobby.
///
/// Lifecycle (PUBLIC API):
///   - Show(seconds, reason)  : tampilkan panel + mulai countdown.
///   - Hide()                 : sembunyikan paksa (jarang dipakai).
///   - IsShowing              : true kalau sedang countdown.
///
/// Saat countdown habis, otomatis call LobbyReturn.Go().
/// </summary>
public class ReturnToLobbyUI : MonoBehaviour
{
    [Header("UI References")]
    [Tooltip("Root panel yang di-toggle on/off.")]
    [SerializeField] private GameObject panel;
    [Tooltip("Text untuk countdown (mis. '5').")]
    [SerializeField] private TMP_Text countdownText;
    [Tooltip("Text alasan (mis. 'Permainan selesai!' / 'Player keluar...').")]
    [SerializeField] private TMP_Text reasonText;

    private float _remaining;
    private bool _running;

    public bool IsShowing => _running;

    private void Awake()
    {
        if (panel != null) panel.SetActive(false);
    }

    /// <summary>
    /// Tampilkan panel + mulai countdown lokal.
    /// </summary>
    public void Show(float seconds, string reason)
    {
        _remaining = Mathf.Max(0f, seconds);
        _running = true;

        if (panel != null) panel.SetActive(true);
        if (reasonText != null) reasonText.text = reason ?? "";
        UpdateCountdownLabel();
    }

    public void Hide()
    {
        _running = false;
        if (panel != null) panel.SetActive(false);
    }

    private void Update()
    {
        if (!_running) return;

        _remaining -= Time.deltaTime;

        if (_remaining <= 0f)
        {
            _running = false;
            UpdateCountdownLabel();
            LobbyReturn.Go();
            return;
        }

        UpdateCountdownLabel();
    }

    private void UpdateCountdownLabel()
    {
        if (countdownText == null) return;
        int displayed = Mathf.CeilToInt(Mathf.Max(0f, _remaining));
        countdownText.text = displayed.ToString();
    }
}
