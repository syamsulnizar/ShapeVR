using UnityEngine;
using TMPro;

/// <summary>
/// UI panel untuk colocation. Wire-able dari Inspector.
///
/// Cara setup:
///   1. Drag GameObject RetryButton -> OnClick() -> ColocationManager.RetryColocation
///   2. Drag GameObject ContinueButton -> OnClick() -> ColocationManager.ContinueAnyway
///
/// Public method di sini hanya untuk ColocationManager memanggil dari script.
/// Tombol UI di-wire langsung ke ColocationManager via Inspector.
/// </summary>
public class ColocationCanvasUI : MonoBehaviour
{
    [Header("Refs (wire di Inspector)")]
    [SerializeField] private GameObject panel;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private TMP_Text reasonText;
    [Tooltip("Parent GameObject yang berisi RetryButton + ContinueButton. Akan SetActive(true/false).")]
    [SerializeField] private GameObject buttonsRoot;

    [Header("Strings")]
    [SerializeField] private string validatingTitle = "Validating colocation...";
    [SerializeField] private string validatingReason = "Make sure both players are in the same room.";
    [SerializeField] private string failedTitle = "Colocation failed";

    private void Awake()
    {
        if (panel != null) panel.SetActive(false);
    }

    public void ShowValidating()
    {
        if (panel != null) panel.SetActive(true);
        if (statusText != null) statusText.text = validatingTitle;
        if (reasonText != null) reasonText.text = validatingReason;
        if (buttonsRoot != null) buttonsRoot.SetActive(false);
    }

    public void ShowFailedWithButtons(string reason)
    {
        if (panel != null) panel.SetActive(true);
        if (statusText != null) statusText.text = failedTitle;
        if (reasonText != null) reasonText.text = reason + "\n\nRetry or continue anyway.";
        if (buttonsRoot != null) buttonsRoot.SetActive(true);
    }

    public void SetCustomMessage(string title, string reason)
    {
        if (panel != null) panel.SetActive(true);
        if (statusText != null) statusText.text = title;
        if (reasonText != null) reasonText.text = reason;
        if (buttonsRoot != null) buttonsRoot.SetActive(false);
    }

        public void Hide()
    {
        if (panel != null) panel.SetActive(false);
    }
}
