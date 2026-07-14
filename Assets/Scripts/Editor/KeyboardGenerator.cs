using UnityEngine;
using UnityEditor;
using TMPro;
using Oculus.Interaction;
using System;

public class KeyboardGenerator : EditorWindow
{
    [MenuItem("Tools/Generate 3D Keyboard")]
    public static void GenerateKeyboard()
    {
        // 1. Find template in the scene
        GameObject templateButton = null;
        var interactables = FindObjectsByType<PokeInteractable>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var p in interactables)
        {
            if (p.gameObject.name == "PokeInteractable - Play")
            {
                templateButton = p.gameObject;
                break;
            }
        }

        if (templateButton == null)
        {
            Debug.LogError("Template button 'PokeInteractable - Play' tidak ditemukan di scene!");
            return;
        }

        // 2. Find parent "Button Room" to place the keyboard in the same location
        GameObject roomButtons = GameObject.Find("OVRCameraRig/TrackingSpace/CenterEyeAnchor/HoverButtons/Button Room");
        Transform parentTransform = null;
        Vector3 spawnPos = Vector3.zero;
        Quaternion spawnRot = Quaternion.identity;

        if (roomButtons != null)
        {
            parentTransform = roomButtons.transform.parent;
            spawnPos = roomButtons.transform.localPosition;
            spawnRot = roomButtons.transform.localRotation;
        }
        else
        {
            Debug.LogWarning("GameObject 'Button Room' tidak ditemukan, keyboard akan diletakkan di root scene.");
        }

        // 3. Create Keyboard Container
        GameObject keyboardRoot = new GameObject("3DKeyboard");
        if (parentTransform != null)
        {
            keyboardRoot.transform.SetParent(parentTransform, false);
            keyboardRoot.transform.localPosition = spawnPos;
            keyboardRoot.transform.localRotation = spawnRot;
        }
        keyboardRoot.transform.localScale = Vector3.one;

        Undo.RegisterCreatedObjectUndo(keyboardRoot, "Generate 3D Keyboard");

        // Spacing & Layout
        float colSpacing = 0.054f;
        float rowSpacing = 0.034f;
        float scaleFactor = 0.33f;
        float centerY = -0.05f;

        string[] layout = new string[]
        {
            "1234567890",
            "QWERTYUIOP",
            "ASDFGHJKL",
            "ZXCVBNM"
        };

        // Background Plate
        GameObject bgObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
        bgObj.name = "KeyboardBackground";
        bgObj.transform.SetParent(keyboardRoot.transform, false);
        bgObj.transform.localPosition = new Vector3(0f, -0.03f, 0.015f);
        bgObj.transform.localRotation = Quaternion.identity;
        bgObj.transform.localScale = new Vector3(10.8f * colSpacing, 0.35f, 0.01f);

        var bgCollider = bgObj.GetComponent<Collider>();
        if (bgCollider != null) DestroyImmediate(bgCollider);

        var bgRenderer = bgObj.GetComponent<MeshRenderer>();
        if (bgRenderer != null)
        {
            bgRenderer.material.color = new Color(0.12f, 0.12f, 0.15f, 0.95f);
        }

        // Font Asset from template
        TextMeshPro templateText = templateButton.GetComponentInChildren<TextMeshPro>();

        // Title Text
        GameObject titleObj = new GameObject("TitleText");
        titleObj.transform.SetParent(keyboardRoot.transform, false);
        titleObj.transform.localPosition = new Vector3(0f, 0.12f, 0f);
        titleObj.transform.localRotation = Quaternion.identity;
        var titleTmp = titleObj.AddComponent<TextMeshPro>();
        ConfigureText(titleTmp, templateText, "ENTER YOUR PLAYER ID", 0.22f, Color.white);

        // Display Text
        GameObject displayObj = new GameObject("DisplayText");
        displayObj.transform.SetParent(keyboardRoot.transform, false);
        displayObj.transform.localPosition = new Vector3(0f, 0.06f, 0f);
        displayObj.transform.localRotation = Quaternion.identity;
        var displayTmp = displayObj.AddComponent<TextMeshPro>();
        ConfigureText(displayTmp, templateText, "Enter ID...", 0.30f, new Color(0.6f, 0.6f, 0.6f));

        // Status Text
        GameObject statusObj = new GameObject("StatusText");
        statusObj.transform.SetParent(keyboardRoot.transform, false);
        statusObj.transform.localPosition = new Vector3(0f, 0f, 0f);
        statusObj.transform.localRotation = Quaternion.identity;
        var statusTmp = statusObj.AddComponent<TextMeshPro>();
        ConfigureText(statusTmp, templateText, "", 0.16f, Color.white);

        // Spawn Keys
        float currentScale = templateButton.transform.localScale.x * scaleFactor;

        for (int r = 0; r < layout.Length; r++)
        {
            string rowKeys = layout[r];
            int numKeys = rowKeys.Length;
            float startX = -(numKeys - 1) * colSpacing / 2f;

            for (int i = 0; i < numKeys; i++)
            {
                string label = rowKeys[i].ToString();
                float localX = startX + i * colSpacing;
                float localY = centerY - r * rowSpacing;

                GameObject keyBtn = Instantiate(templateButton);
                keyBtn.name = "Key_" + label;
                keyBtn.transform.SetParent(keyboardRoot.transform, false);
                keyBtn.transform.localPosition = new Vector3(localX, localY, 0f);
                keyBtn.transform.localScale = new Vector3(currentScale, currentScale, currentScale);
                keyBtn.transform.localRotation = Quaternion.identity;
                keyBtn.SetActive(true);

                SetupKeyVisual(keyBtn, label);
                ClearPersistentListeners(keyBtn);
            }
        }

        // Action Keys
        float actionColSpacing = colSpacing * 1.8f;
        float actionStartX = -1.5f * actionColSpacing;
        float actionY = centerY - 4f * rowSpacing;
        float actionScaleX = currentScale * 1.5f;

        string[] actionLabels = { "BACK", "BKSP", "CLR", "SUBMIT" };
        Color[] actionColors = {
            new Color(0.6f, 0.2f, 0.2f),
            new Color(0.4f, 0.4f, 0.4f),
            new Color(0.4f, 0.4f, 0.4f),
            new Color(0.2f, 0.6f, 0.2f)
        };

        for (int i = 0; i < actionLabels.Length; i++)
        {
            string label = actionLabels[i];
            GameObject actBtn = Instantiate(templateButton);

            actBtn.name = "Action_" + label;
            actBtn.transform.SetParent(keyboardRoot.transform, false);
            actBtn.transform.localPosition = new Vector3(actionStartX + i * actionColSpacing, actionY, 0f);
            actBtn.transform.localScale = new Vector3(actionScaleX, currentScale, currentScale);
            actBtn.transform.localRotation = Quaternion.identity;
            actBtn.SetActive(true);

            SetupKeyVisual(actBtn, label);
            SetButtonColor(actBtn, actionColors[i]);
            ClearPersistentListeners(actBtn);
        }

        // Mark the scene as dirty so it can be saved
        EditorUtility.SetDirty(keyboardRoot);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        Debug.Log("Keyboard 3D berhasil di-generate di scene! Silakan sesuaikan surface/collider/event wrapper secara manual.");
    }

    private static void ClearPersistentListeners(GameObject btnObj)
    {
        var wrapper = btnObj.GetComponent<InteractableUnityEventWrapper>();
        if (wrapper == null) return;

        SerializedObject so = new SerializedObject(wrapper);
        string[] eventNames = { "WhenHover", "WhenUnhover", "WhenSelect", "WhenUnselect" };
        foreach (var evtName in eventNames)
        {
            SerializedProperty prop = so.FindProperty(evtName);
            if (prop != null)
            {
                SerializedProperty calls = prop.FindPropertyRelative("m_PersistentCalls.m_Calls");
                if (calls != null) calls.ClearArray();
            }
        }
        so.ApplyModifiedProperties();
    }

    private static void ConfigureText(TextMeshPro target, TextMeshPro source, string text, float fontSize, Color color)
    {
        if (source != null)
        {
            target.font = source.font;
            target.fontSharedMaterial = source.fontSharedMaterial;
        }
        target.text = text;
        target.fontSize = fontSize;
        target.alignment = TextAlignmentOptions.Center;
        target.color = color;
    }

    private static void SetupKeyVisual(GameObject btnObj, string label)
    {
        Transform visuals = btnObj.transform.Find("Visuals");
        if (visuals != null)
        {
            Transform buttonVisual = visuals.Find("ButtonVisual");
            if (buttonVisual != null)
            {
                Transform textObj = buttonVisual.Find("Text (TMP)");
                if (textObj != null)
                {
                    var tmp = textObj.GetComponent<TextMeshPro>();
                    if (tmp != null) tmp.text = label;
                }
            }
        }
    }

    private static void SetButtonColor(GameObject btnObj, Color color)
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
                    if (renderer != null && renderer.sharedMaterial != null)
                    {
                        renderer.sharedMaterial.color = color;
                    }
                }
            }
        }
    }
}
