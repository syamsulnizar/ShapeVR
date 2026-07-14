using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using Unity.Netcode;
using Unity.Collections;

public class PlayerDataSaver : NetworkBehaviour
{
    public static PlayerDataSaver Instance { get; private set; }

    // Save the ID input from Lobby
    public static string playerInputId = "";

    [Header("Google Sheets Integration")]
    [Tooltip("Web App URL from Google Apps Script.")]
    [SerializeField] private string googleSheetsUrl = "https://script.google.com/macros/s/AKfycby4Knc4pX_8Cy-we0xpP9P7gUBGOvDQ1ULVDT30PpaXXfKWRCHCeJKc5x17fcLCntfrCg/exec";

    [Header("Debug")]
    [SerializeField] private int correctAnswers = 0;

    // NetworkVariable to synchronize the same Session ID between both players
    private readonly NetworkVariable<FixedString64Bytes> sessionId = new NetworkVariable<FixedString64Bytes>(
        writePerm: NetworkVariableWritePermission.Server
    );

    private float startTime;
    private bool dataSaved = false;
    private readonly List<string> mistakeLogs = new List<string>();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        // Only Server/Host generates a unique Session ID at the start of the game
        if (IsServer)
        {
            sessionId.Value = Guid.NewGuid().ToString();
        }
    }

    private void Start()
    {
        startTime = Time.time;
        dataSaved = false;
        mistakeLogs.Clear();

        // Find GameManager and subscribe to the Won event
        GameManager gameManager = FindFirstObjectByType<GameManager>();
        if (gameManager != null)
        {
            gameManager.Won.AddListener(OnGameWon);
            Debug.Log("[PlayerDataSaver] Successfully subscribed to GameManager.Won event.");
        }
        else
        {
            Debug.LogWarning("[PlayerDataSaver] GameManager not found in the scene.");
        }
    }

    public void IncrementCorrectAnswers()
    {
        correctAnswers++;
        Debug.Log($"[PlayerDataSaver] Correct placements incremented: {correctAnswers}");
    }

    public void DecrementCorrectAnswers()
    {
        correctAnswers = Mathf.Max(0, correctAnswers - 1);
        Debug.Log($"[PlayerDataSaver] Correct placements decremented: {correctAnswers}");
    }

    public void RecordMistake(string shapeName)
    {
        float elapsed = Time.time - startTime;
        int minutes = Mathf.FloorToInt(elapsed / 60f);
        int seconds = Mathf.FloorToInt(elapsed % 60f);
        string timeStr = minutes > 0 ? $"{minutes}m {seconds}s" : $"{seconds}s";

        string cleanName = string.IsNullOrWhiteSpace(shapeName) ? "Shape" : shapeName;
        string logMessage = $"At {timeStr} ({cleanName})";
        mistakeLogs.Add(logMessage);
        
        Debug.Log($"[PlayerDataSaver] Mistake logged: {logMessage}");
    }

    private string GetMistakeLogsString()
    {
        if (mistakeLogs.Count == 0)
        {
            return "No mistakes";
        }
        return string.Join(", ", mistakeLogs);
    }

    private string GetSessionId()
    {
        if (NetworkManager.Singleton != null && IsSpawned)
        {
            return sessionId.Value.ToString();
        }
        return "offline_" + DateTime.UtcNow.Ticks;
    }

    private void OnGameWon()
    {
        if (dataSaved) return;
        dataSaved = true;

        SavePlayerData();
    }

    private void SavePlayerData()
    {
        // 1. Determine Player Role (Player 1 or Player 2)
        string playerRole = "Player 1";
        bool isPlayer1 = true;
        var nm = NetworkManager.Singleton;
        if (nm != null && (nm.IsHost || nm.IsClient))
        {
            ulong clientId = nm.LocalClientId;
            isPlayer1 = clientId == 0;
            playerRole = isPlayer1 ? "Player 1" : "Player 2";
        }

        // 2. Play Time (GMT)
        string playTime = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " GMT";

        // 3. Location (Where it is played)
        string location = GetCurrentLocation();

        // 4. Completion Time
        float completionTime = Time.time - startTime;

        // 5. Correct Answers
        int correct = correctAnswers;

        // 6. Incorrect Answers
        int incorrect = 0;
        if (ShapeErrorLogger.Instance != null)
        {
            incorrect = ShapeErrorLogger.Instance.WrongReleaseCount;
        }

        string sessionGuid = GetSessionId();
        string mistakeLogsStr = GetMistakeLogsString();

        Debug.Log($"[PlayerDataSaver] Session Completed: Role={playerRole}, PlayTime={playTime}, Location={location}, Duration={completionTime:0.00}s, Correct={correct}, Incorrect={incorrect}, Mistakes={mistakeLogsStr}, SessionID={sessionGuid}");

        // Send data to Google Sheets (and local CSV after finished/failed)
        SendToGoogleSheets(playerRole, playTime, location, completionTime, correct, incorrect, sessionGuid, mistakeLogsStr, isPlayer1);
    }

    private string GetCurrentLocation()
    {
        string sceneName = SceneManager.GetActiveScene().name;

        if (sceneName == "Passthrough")
        {
            return "Passthrough";
        }
        else if (sceneName == "Virtual Mic Off")
        {
            return "Virtual Mic Off";
        }
        else if (sceneName == "Virtual Mic On")
        {
            return "Virtual Mic On";
        }
        else if (sceneName == "Virtual Choose Room")
        {
            if (RoomOption.Instance != null)
            {
                switch (RoomOption.Instance.CurrentRoom)
                {
                    case RoomOption.Room.Sky:
                        return "Virtual Choose Room-Space";
                    case RoomOption.Room.School:
                        return "Virtual Choose Room-School";
                    case RoomOption.Room.Office:
                        return "Virtual Choose Room-Office";
                    case RoomOption.Room.LivingRoom:
                        return "Virtual Choose Room-Living Room";
                }
            }
            return "Virtual Choose Room";
        }

        return sceneName;
    }

    private void SaveLocalCSV(string playerId, string pairId, string playerRole, string playTime, string location, float completionTime, int correct, int incorrect, string mistakeLogsStr, string sessionGuid, bool isPlayer1)
    {
        string header = "PlayerID,PairID,PlayerRole,PlayTimeGMT,Location,CompletionTime,CorrectAnswers,IncorrectAnswers,MistakeLogs,SessionID";
        string persistentPath = Path.Combine(Application.persistentDataPath, "PlayerData.csv");

        string finalPlayerId = string.IsNullOrEmpty(playerId) ? playerInputId : playerId;
        if (string.IsNullOrEmpty(finalPlayerId))
        {
            finalPlayerId = "Player";
        }

        string finalPairId = pairId;
        if (string.IsNullOrEmpty(finalPairId))
        {
            finalPairId = GetLocalFallbackPairId(persistentPath, sessionGuid);
        }

        // Mistake logs are wrapped in double quotes ("...") so that commas inside do not break the CSV columns
        string csvLine = $"{finalPlayerId},{finalPairId},{playerRole},{playTime},{location},{completionTime:0.00},{correct},{incorrect},\"{mistakeLogsStr}\",{sessionGuid}";
        WriteRowToFile(persistentPath, header, csvLine);

#if UNITY_EDITOR
        string projectPath = Path.Combine(Application.dataPath, "PlayerData.csv");
        if (string.IsNullOrEmpty(playerId) || string.IsNullOrEmpty(pairId))
        {
            finalPairId = GetLocalFallbackPairId(projectPath, sessionGuid);
        }
        csvLine = $"{finalPlayerId},{finalPairId},{playerRole},{playTime},{location},{completionTime:0.00},{correct},{incorrect},\"{mistakeLogsStr}\",{sessionGuid}";
        WriteRowToFile(projectPath, header, csvLine);
#endif
    }

    private string GetLocalFallbackPairId(string filePath, string sessionGuid)
    {
        if (!File.Exists(filePath)) return "1";

        try
        {
            string[] lines = File.ReadAllLines(filePath);
            if (lines.Length <= 1) return "1";

            int maxPair = 0;
            string existingPairId = null;

            for (int i = 1; i < lines.Length; i++)
            {
                string[] parts = lines[i].Split(',');
                if (parts.Length > 9)
                {
                    string pairStr = parts[1];
                    string sessId = parts[9].Replace("\"", "").Trim();

                    if (int.TryParse(pairStr, out int pairVal))
                    {
                        if (pairVal > maxPair) maxPair = pairVal;
                    }

                    if (sessId == sessionGuid.Trim())
                    {
                        existingPairId = pairStr;
                    }
                }
            }

            if (!string.IsNullOrEmpty(existingPairId))
            {
                return existingPairId;
            }
            return (maxPair + 1).ToString();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[PlayerDataSaver] Error calculating fallback PairID: {ex.Message}");
            return "1";
        }
    }

    private void WriteRowToFile(string filePath, string header, string csvLine)
    {
        try
        {
            bool writeHeader = !File.Exists(filePath);
            using (StreamWriter writer = new StreamWriter(filePath, true))
            {
                if (writeHeader)
                {
                    writer.WriteLine(header);
                }
                writer.WriteLine(csvLine);
            }
            Debug.Log($"[PlayerDataSaver] Data appended to local CSV: {filePath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[PlayerDataSaver] Error writing CSV to {filePath}: {ex.Message}");
        }
    }

    private void SendToGoogleSheets(string playerRole, string playTime, string location, float completionTime, int correct, int incorrect, string sessionGuid, string mistakeLogsStr, bool isPlayer1)
    {
        PlayerDataPayload payload = new PlayerDataPayload
        {
            playerId = string.IsNullOrEmpty(playerInputId) ? "Player" : playerInputId,
            playerRole = playerRole,
            playTime = playTime,
            location = location,
            completionTime = completionTime,
            correctAnswers = correct,
            incorrectAnswers = incorrect,
            sessionId = sessionGuid,
            mistakeLogs = mistakeLogsStr
        };

        string json = JsonUtility.ToJson(payload);
        StartCoroutine(PostRequest(json, playerRole, playTime, location, completionTime, correct, incorrect, sessionGuid, mistakeLogsStr, isPlayer1));
    }

    private IEnumerator PostRequest(string json, string playerRole, string playTime, string location, float completionTime, int correct, int incorrect, string sessionGuid, string mistakeLogsStr, bool isPlayer1)
    {
        if (string.IsNullOrEmpty(googleSheetsUrl))
        {
            Debug.Log("[PlayerDataSaver] Google Sheets URL is not configured. Saving local CSV only.");
            SaveLocalCSV(null, null, playerRole, playTime, location, completionTime, correct, incorrect, mistakeLogsStr, sessionGuid, isPlayer1);
            yield break;
        }

        using (UnityWebRequest request = new UnityWebRequest(googleSheetsUrl, "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                Debug.Log("[PlayerDataSaver] Successfully uploaded data to Google Sheets: " + request.downloadHandler.text);

                try
                {
                    GoogleSheetsResponse response = JsonUtility.FromJson<GoogleSheetsResponse>(request.downloadHandler.text);
                    if (response != null && response.result == "success")
                    {
                        string assignedId = !string.IsNullOrEmpty(response.playerId) ? response.playerId : null;
                        string assignedPairId = response.pairId > 0 ? response.pairId.ToString() : null;
                        SaveLocalCSV(assignedId, assignedPairId, playerRole, playTime, location, completionTime, correct, incorrect, mistakeLogsStr, sessionGuid, isPlayer1);
                    }
                    else
                    {
                        SaveLocalCSV(null, null, playerRole, playTime, location, completionTime, correct, incorrect, mistakeLogsStr, sessionGuid, isPlayer1);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[PlayerDataSaver] Failed to parse Google Sheets response IDs: " + ex.Message);
                    SaveLocalCSV(null, null, playerRole, playTime, location, completionTime, correct, incorrect, mistakeLogsStr, sessionGuid, isPlayer1);
                }
            }
            else
            {
                Debug.LogWarning("[PlayerDataSaver] Failed to upload data to Google Sheets: " + request.error + ". Saving locally.");
                SaveLocalCSV(null, null, playerRole, playTime, location, completionTime, correct, incorrect, mistakeLogsStr, sessionGuid, isPlayer1);
            }
        }
    }

    [Serializable]
    private class PlayerDataPayload
    {
        public string playerId;
        public string playerRole;
        public string playTime;
        public string location;
        public float completionTime;
        public int correctAnswers;
        public int incorrectAnswers;
        public string sessionId;
        public string mistakeLogs;
    }

    [Serializable]
    private class GoogleSheetsResponse
    {
        public string result;
        public string playerId;
        public int pairId;
        public string message;
    }
}
