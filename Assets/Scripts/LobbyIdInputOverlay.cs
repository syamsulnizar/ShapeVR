using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using TMPro;
using Oculus.Interaction;
using UnityEngine.Events;

public class LobbyIdInputOverlay : MonoBehaviour
{
    public static LobbyIdInputOverlay Instance { get; private set; }

    [Header("Configuration")]
    [SerializeField] private string googleSheetsUrl = "https://script.google.com/macros/s/AKfycby4Knc4pX_8Cy-we0xpP9P7gUBGOvDQ1ULVDT30PpaXXfKWRCHCeJKc5x17fcLCntfrCg/exec";

    [SerializeField] private GameObject keyboardContainer;
    [SerializeField] private TextMeshPro idDisplayText;
    [SerializeField] private TextMeshPro statusText;
    [SerializeField] private GameObject submitButtonObj;
    [SerializeField] private string currentInput = "";
    private Action onValidId;
    private GameObject roomButtons;
    [SerializeField] private UnityEvent onValidIDEvent;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    public void ShowOverlay(string roomType, string sceneName, Action onValidIdCallback, GameObject roomButtonsRoot)
    {
        onValidId = onValidIdCallback;
        roomButtons = roomButtonsRoot;
        currentInput = "";

        // Sembunyikan tombol room
        if (roomButtons != null) roomButtons.SetActive(false);

        // Setup dan hubungkan keyboard yang ada di scene
        SetupSceneKeyboard();
    }

    private void SetupSceneKeyboard()
    {
        if (keyboardContainer == null)
        {
            // Cari GameObject "3DKeyboard" di scene
            keyboardContainer = GameObject.Find("3DKeyboard");
            if (keyboardContainer == null)
            {
                keyboardContainer = GameObject.Find("OVRCameraRig/TrackingSpace/CenterEyeAnchor/HoverButtons/3DKeyboard");
            }
        }

        if (keyboardContainer == null)
        {
            Debug.LogError("[LobbyIdInputOverlay] Scene keyboard '3DKeyboard' not found! Please generate it using 'Tools -> Generate 3D Keyboard' first.");
            // Fallback langsung masuk jika keyboard tidak ditemukan agar game tidak stuck
            onValidId?.Invoke();
            return;
        }

        // Cari DisplayText dan StatusText
        Transform dispTrans = keyboardContainer.transform.Find("DisplayText");
        if (dispTrans != null) idDisplayText = dispTrans.GetComponent<TextMeshPro>();

        Transform statusTrans = keyboardContainer.transform.Find("StatusText");
        if (statusTrans != null) statusText = statusTrans.GetComponent<TextMeshPro>();

        // Cari dan setup callback tombol-tombol
        foreach (Transform child in keyboardContainer.transform)
        {
            if (child.name.StartsWith("Key_"))
            {
                string label = child.name.Substring(4); // e.g. "A", "1"
                SetupKeyCallbacks(child.gameObject, label, () => AppendChar(label));
            }
            else if (child.name.StartsWith("Action_"))
            {
                string actionName = child.name.Substring(7); // e.g. "BACK", "SUBMIT"
                if (actionName == "BACK")
                {
                    SetupKeyCallbacks(child.gameObject, "BACK", CancelInput);
                }
                else if (actionName == "BKSP")
                {
                    SetupKeyCallbacks(child.gameObject, "BKSP", BackspaceChar);
                }
                else if (actionName == "CLR")
                {
                    SetupKeyCallbacks(child.gameObject, "CLR", ClearInput);
                }
                else if (actionName == "SUBMIT")
                {
                    submitButtonObj = child.gameObject;
                    SetupKeyCallbacks(child.gameObject, "SUBMIT", SubmitId);
                }
            }
        }

        // Tampilkan keyboard
        keyboardContainer.SetActive(true);
        UpdateDisplay();
    }

    private void SetupKeyCallbacks(GameObject btnObj, string label, Action onClickAction)
    {
        try
        {
            // Pastikan wrapper event sudah ada
            var wrapper = btnObj.GetComponent<InteractableUnityEventWrapper>();
            if (wrapper == null)
            {
                wrapper = btnObj.AddComponent<InteractableUnityEventWrapper>();
                var poke = btnObj.GetComponent<PokeInteractable>();
                if (poke != null) wrapper.InjectInteractableView(poke);
            }

            // Bersihkan listener runtime lama dan daftarkan listener baru
            wrapper.WhenUnselect.RemoveAllListeners();
            wrapper.WhenUnselect.AddListener(() => onClickAction());
        }
        catch (Exception ex)
        {
            Debug.LogError($"[LobbyIdInputOverlay] Error setting up key {label}: {ex.Message}");
        }
    }

    private void SetButtonColor(GameObject btnObj, Color color)
    {
        try
        {
            Transform visuals = btnObj.transform.Find("Visuals");
            if (visuals != null)
            {
                Transform buttonVisual = visuals.Find("ButtonVisual");
                if (buttonVisual != null)
                {
                    Transform buttonPanel = buttonVisual.Find("ButtonPanel");
                    if (buttonPanel != null)
                    {
                        var renderer = buttonPanel.GetComponent<MeshRenderer>();
                        if (renderer != null && renderer.material != null)
                        {
                            renderer.material.color = color;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[LobbyIdInputOverlay] Failed to set button color: " + ex.Message);
        }
    }

    private void AppendChar(string c)
    {
        if (currentInput.Length >= 15) return;
        currentInput += c;
        UpdateDisplay();
    }

    private void BackspaceChar()
    {
        if (currentInput.Length > 0)
        {
            currentInput = currentInput.Substring(0, currentInput.Length - 1);
        }
        UpdateDisplay();
    }

    private void ClearInput()
    {
        currentInput = "";
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        if (idDisplayText != null)
        {
            if (string.IsNullOrEmpty(currentInput))
            {
                idDisplayText.text = "Enter ID...";
                idDisplayText.color = new Color(0.6f, 0.6f, 0.6f);
            }
            else
            {
                idDisplayText.text = currentInput;
                idDisplayText.color = Color.white;
            }
        }
        if (statusText != null) statusText.text = "";
    }

    private void CancelInput()
    {
        if (roomButtons != null) roomButtons.SetActive(true);
        if (keyboardContainer != null) keyboardContainer.SetActive(false);
    }

    private void SubmitId()
    {
        string id = currentInput.Trim();
        if (string.IsNullOrEmpty(id))
        {
            if (statusText != null)
            {
                statusText.text = "ID cannot be empty";
                statusText.color = Color.red;
            }
            return;
        }

        StartCoroutine(CheckIdOnServer(id));
    }

    private void SetSubmitButtonActive(bool active)
    {
        if (submitButtonObj != null)
        {
            var poke = submitButtonObj.GetComponent<PokeInteractable>();
            if (poke != null) poke.enabled = active;
            SetButtonColor(submitButtonObj, active ? new Color(0.2f, 0.6f, 0.2f) : new Color(0.3f, 0.3f, 0.3f));
        }
    }

    private IEnumerator CheckIdOnServer(string id)
    {
        string currentGmtTime = DateTime.UtcNow.ToString("dd/MM/yyyy HH:mm:ss") + " GMT";
        
        // Buat payload untuk POST request
        ReservePayload payload = new ReservePayload
        {
            action = "reserveId",
            playerId = id,
            playTime = currentGmtTime,
            sessionId = "LobbyReservation"
        };
        
        string json = JsonUtility.ToJson(payload);

        if (statusText != null)
        {
            statusText.text = "Checking and reserving ID...";
            statusText.color = Color.white;
        }
        SetSubmitButtonActive(false);

        using (UnityWebRequest request = new UnityWebRequest(googleSheetsUrl, "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = 8;
            
            yield return request.SendWebRequest();

            SetSubmitButtonActive(true);

            if (request.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    IdCheckResponse response = JsonUtility.FromJson<IdCheckResponse>(request.downloadHandler.text);
                    if (response != null)
                    {
                        if (response.result == "success")
                        {
                            PlayerDataSaver.playerInputId = id;
                            if (keyboardContainer != null) keyboardContainer.SetActive(false);
                            onValidIDEvent?.Invoke();
                            onValidId?.Invoke();
                        }
                        else
                        {
                            if (statusText != null)
                            {
                                statusText.text = response.message.Contains("taken") || response.message.Contains("used") ? "ID already used, please use another ID" : response.message;
                                statusText.color = Color.red;
                            }
                        }
                    }
                    else
                    {
                        Debug.LogWarning("[LobbyIdInputOverlay] Parse error. Proceeding anyway.");
                        PlayerDataSaver.playerInputId = id;
                        if (keyboardContainer != null) keyboardContainer.SetActive(false);
                        onValidIDEvent?.Invoke();
                        onValidId?.Invoke();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[LobbyIdInputOverlay] Parse error exception: " + ex.Message + ". Proceeding anyway.");
                    PlayerDataSaver.playerInputId = id;
                    if (keyboardContainer != null) keyboardContainer.SetActive(false);
                    onValidIDEvent?.Invoke();
                    onValidId?.Invoke();
                }
            }
            else
            {
                Debug.LogWarning("[LobbyIdInputOverlay] Network error: " + request.error + ". Proceeding anyway.");
                PlayerDataSaver.playerInputId = id;
                if (keyboardContainer != null) keyboardContainer.SetActive(false);
                onValidIDEvent?.Invoke();
                onValidId?.Invoke();
            }
        }
    }

    [Serializable]
    private class ReservePayload
    {
        public string action;
        public string playerId;
        public string playTime;
        public string sessionId;
    }

    [Serializable]
    private class IdCheckResponse
    {
        public string result;
        public bool exists;
        public string message;
    }
}
