using UnityEngine;
using TMPro;

/// <summary>
/// UI panel for colocation. Wire-able from the Inspector.
///
/// Setup instructions:
///   1. Drag GameObject RetryButton -> OnClick() -> ColocationManager.RetryColocation
///   2. Drag GameObject ContinueButton -> OnClick() -> ColocationManager.ContinueAnyway
///
/// Public methods here are only for ColocationManager to call from the script.
/// UI buttons are wired directly to ColocationManager via the Inspector.
/// </summary>
public class ColocationCanvasUI : MonoBehaviour
{
    [Header("Refs (wired in Inspector)")]
    [SerializeField] private GameObject panel;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private TMP_Text reasonText;
    [Tooltip("Parent GameObject containing RetryButton + ContinueButton. Will SetActive(true/false).")]
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
