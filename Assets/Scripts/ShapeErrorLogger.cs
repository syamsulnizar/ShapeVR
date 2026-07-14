using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Local-only VR toast for shape placement mistakes.
/// Creates its own world-space canvas in front of the active VR camera.
/// </summary>
public class ShapeErrorLogger : MonoBehaviour
{
    public static ShapeErrorLogger Instance { get; private set; }

    [Header("Local Data")]
    [SerializeField] private int wrongReleaseCount;

    [Header("Toast")]
    [SerializeField] private float showSeconds = 1.75f;
    [SerializeField] private Vector3 startLocalPosition = new Vector3(0f, -0.22f, 1.15f);
    [SerializeField] private Vector3 endLocalPosition = new Vector3(0f, 0.05f, 1.15f);
    [SerializeField] private Vector2 canvasSize = new Vector2(760f, 170f);
    [SerializeField] private float canvasScale = 0.001f;
    [SerializeField] private string uiLayerName = "UIPriority";
    [SerializeField] private TMP_Text totalMistake;

    [Header("Panel Style")]
    public Sprite panelBackground;
    [SerializeField] private Color panelColor = new Color(0.55f, 0.03f, 0.03f, 0.82f);
    [SerializeField] private Image.Type panelImageType = Image.Type.Sliced;
    [SerializeField] private float pixelsPerUnitMultiplier = 3f;

    [Header("Text Style")]
    public TMP_FontAsset messageFont;
    [SerializeField] private Color textColor = Color.white;
    [SerializeField] private float messageFontSize = 34f;

    private Canvas _canvas;
    private CanvasGroup _canvasGroup;
    private RectTransform _canvasRect;
    private TMP_Text _messageText;
    private Coroutine _toastRoutine;

    public int WrongReleaseCount => wrongReleaseCount;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        BuildCanvas();
        HideImmediate();
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    public void ReportWrongRelease(string shapeName)
    {
        ReportWrongReleaseDetailed(shapeName, "wrong position");
    }

    public void ReportWrongReleaseDetailed(string shapeName, string targetName)
    {
        wrongReleaseCount++;

        string cleanShape = CleanShapeName(shapeName);
        string cleanTarget = CleanShapeName(targetName);

        // English format: "Donut placed into Triangle"
        string mistakeDetail = $"{cleanShape} placed into {cleanTarget}";

        if (PlayerDataSaver.Instance != null)
        {
            PlayerDataSaver.Instance.RecordMistake(mistakeDetail);
        }

        string message = $"Error: {cleanShape} placed in {cleanTarget}\nMistakes: {wrongReleaseCount}";
        Debug.LogWarning($"[ShapeErrorLogger] {message.Replace('\n', ' ')}");

        if (_toastRoutine != null)
            StopCoroutine(_toastRoutine);

        _toastRoutine = StartCoroutine(ShowToast(message));
    }

    private string CleanShapeName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return "Shape";
        
        // Remove "(Clone)" or " (Clone)" or trailing space
        string clean = rawName.Replace("(Clone)", "").Replace(" (Clone)", "").Trim();
        
        // Remove suffix numbers like " (1)" or " 1" using the first space
        int index = clean.IndexOf(" ");
        if (index > 0)
        {
            clean = clean.Substring(0, index);
        }
        
        // Capitalize the first letter
        if (clean.Length > 0)
        {
            clean = char.ToUpper(clean[0]) + clean.Substring(1);
        }
        
        return clean;
    }

    public void TotalMistake()
    {
        totalMistake.text = $"Total errors: {wrongReleaseCount}";
    }

    private void BuildCanvas()
    {
        Camera targetCamera = Camera.main != null ? Camera.main : FindFirstObjectByType<Camera>();

        GameObject canvasObject = new GameObject("Shape Error Toast Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(CanvasGroup));
        canvasObject.transform.SetParent(targetCamera != null ? targetCamera.transform : transform, false);

        _canvasRect = canvasObject.GetComponent<RectTransform>();
        _canvasRect.sizeDelta = canvasSize;
        _canvasRect.localPosition = startLocalPosition;
        _canvasRect.localRotation = Quaternion.identity;
        _canvasRect.localScale = Vector3.one * canvasScale;
        ApplyLayer(canvasObject);

        _canvas = canvasObject.GetComponent<Canvas>();
        _canvas.renderMode = RenderMode.WorldSpace;
        _canvas.worldCamera = targetCamera;

        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 10f;

        _canvasGroup = canvasObject.GetComponent<CanvasGroup>();

        GameObject panelObject = new GameObject("Panel", typeof(RectTransform), typeof(Image));
        panelObject.transform.SetParent(canvasObject.transform, false);

        RectTransform panelRect = panelObject.GetComponent<RectTransform>();
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.one;
        panelRect.offsetMin = Vector2.zero;
        panelRect.offsetMax = Vector2.zero;

        Image panel = panelObject.GetComponent<Image>();
        panel.sprite = panelBackground;
        panel.color = panelColor;
        panel.type = panelBackground != null ? panelImageType : Image.Type.Simple;
        panel.pixelsPerUnitMultiplier = pixelsPerUnitMultiplier;

        GameObject textObject = new GameObject("Message", typeof(RectTransform), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(panelObject.transform, false);

        RectTransform textRect = textObject.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(28f, 18f);
        textRect.offsetMax = new Vector2(-28f, -18f);

        _messageText = textObject.GetComponent<TextMeshProUGUI>();
        if (messageFont != null)
            _messageText.font = messageFont;
        _messageText.alignment = TextAlignmentOptions.Center;
        _messageText.color = textColor;
        _messageText.fontStyle = FontStyles.Bold;
        _messageText.fontSize = messageFontSize;
        _messageText.enableAutoSizing = true;
        _messageText.fontSizeMin = 18f;
        _messageText.fontSizeMax = messageFontSize;
        _messageText.text = "";
    }

    private void ApplyLayer(GameObject root)
    {
        int layer = LayerMask.NameToLayer(uiLayerName);
        if (layer < 0) return;

        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            child.gameObject.layer = layer;
    }

    private IEnumerator ShowToast(string message)
    {
        if (_messageText == null || _canvasRect == null || _canvasGroup == null)
            yield break;

        _messageText.text = message;
        _canvasRect.gameObject.SetActive(true);

        float elapsed = 0f;
        while (elapsed < showSeconds)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / showSeconds);
            float moveT = Mathf.SmoothStep(0f, 1f, t);

            _canvasRect.localPosition = Vector3.Lerp(startLocalPosition, endLocalPosition, moveT);
            _canvasGroup.alpha = t < 0.72f ? 1f : Mathf.InverseLerp(1f, 0.72f, t);

            yield return null;
        }

        HideImmediate();
        _toastRoutine = null;
    }

    private void HideImmediate()
    {
        if (_canvasRect != null)
        {
            _canvasRect.localPosition = startLocalPosition;
            _canvasRect.gameObject.SetActive(false);
        }

        if (_canvasGroup != null)
            _canvasGroup.alpha = 0f;
    }
}
