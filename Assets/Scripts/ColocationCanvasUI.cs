using UnityEngine;
using TMPro;

/// <summary>
/// UI panel untuk colocation validation status.
/// State:
///   - ShowValidating(): tampilkan "Validating colocation..."
///   - ShowFailed(reason): ganti ke pesan failure
///   - Hide(): sembunyikan panel
/// </summary>
public class ColocationCanvasUI : MonoBehaviour
{
    [SerializeField] private GameObject panel;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private TMP_Text reasonText;

    [SerializeField] private string validatingText = "Validating colocation...";
    [SerializeField] private string validatingReasonText = "Make sure both players are in the same room.";

    private void Awake()
    {
        if (panel != null) panel.SetActive(false);
    }

    public void ShowValidating()
    {
        if (panel != null) panel.SetActive(true);
        if (statusText != null) statusText.text = validatingText;
        if (reasonText != null) reasonText.text = validatingReasonText;
    }

    public void ShowFailed(string reason)
    {
        if (panel != null) panel.SetActive(true);
        if (statusText != null) statusText.text = "Colocation failed";
        if (reasonText != null) reasonText.text = reason;
    }

    public void Hide()
    {
        if (panel != null) panel.SetActive(false);
    }
}
