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

    [Header("Google Sheets Integration")]
    [Tooltip("URL Web App dari Google Apps Script.")]
    [SerializeField] private string googleSheetsUrl = "https://script.google.com/macros/s/AKfycby4Knc4pX_8Cy-we0xpP9P7gUBGOvDQ1ULVDT30PpaXXfKWRCHCeJKc5x17fcLCntfrCg/exec";

    [Header("Debug")]
    [SerializeField] private int correctAnswers = 0;

    // NetworkVariable untuk sinkronisasi Session ID yang sama antar kedua player
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
        // Hanya Server/Host yang generate Session ID unik di awal permainan
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

        // Cari GameManager dan daftarkan ke event Won
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
        // 1. Tentukan Player Role (Player 1 atau Player 2)
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

        // 3. Location (Main Di mana)
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

        // Kirim data ke Google Sheets (dan local CSV setelah selesai/gagal)
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

        string finalPlayerId = playerId;
        string finalPairId = pairId;

        // Fallback jika tidak terkoneksi internet / Google Sheets gagal merespon
        if (string.IsNullOrEmpty(finalPlayerId) || string.IsNullOrEmpty(finalPairId))
        {
            GetLocalFallbackIds(persistentPath, isPlayer1, sessionGuid, out string fallbackPlayerId, out string fallbackPairId);
            finalPlayerId = fallbackPlayerId;
            finalPairId = fallbackPairId;
        }

        // Mistake logs dibungkus tanda kutip ganda ("...") agar koma di dalamnya tidak memecah kolom CSV
        string csvLine = $"{finalPlayerId},{finalPairId},{playerRole},{playTime},{location},{completionTime:0.00},{correct},{incorrect},\"{mistakeLogsStr}\",{sessionGuid}";
        WriteRowToFile(persistentPath, header, csvLine);

#if UNITY_EDITOR
        string projectPath = Path.Combine(Application.dataPath, "PlayerData.csv");
        if (string.IsNullOrEmpty(playerId) || string.IsNullOrEmpty(pairId))
        {
            GetLocalFallbackIds(projectPath, isPlayer1, sessionGuid, out string fallbackPlayerId, out string fallbackPairId);
            finalPlayerId = fallbackPlayerId;
            finalPairId = fallbackPairId;
        }
        csvLine = $"{finalPlayerId},{finalPairId},{playerRole},{playTime},{location},{completionTime:0.00},{correct},{incorrect},\"{mistakeLogsStr}\",{sessionGuid}";
        WriteRowToFile(projectPath, header, csvLine);
#endif
    }

    private void GetLocalFallbackIds(string filePath, bool isPlayer1, string sessionGuid, out string fallbackPlayerId, out string fallbackPairId)
    {
        if (!File.Exists(filePath))
        {
            fallbackPlayerId = isPlayer1 ? "1" : "2";
            fallbackPairId = "1";
            return;
        }

        try
        {
            string[] lines = File.ReadAllLines(filePath);
            if (lines.Length <= 1) // Hanya header
            {
                fallbackPlayerId = isPlayer1 ? "1" : "2";
                fallbackPairId = "1";
                return;
            }

            int maxOdd = 0;
            int maxEven = 0;
            int maxPair = 0;
            string existingPairId = null;

            for (int i = 1; i < lines.Length; i++)
            {
                string[] parts = lines[i].Split(',');
                if (parts.Length > 9) // Setidaknya ada 10 kolom (index 0 sampai 9)
                {
                    string idStr = parts[0];
                    string pairStr = parts[1];
                    string sessId = parts[9];

                    // Hapus tanda kutip ganda jika ada pada session ID
                    sessId = sessId.Replace("\"", "").Trim();

                    if (int.TryParse(idStr, out int idVal))
                    {
                        if (idVal % 2 != 0)
                        {
                            if (idVal > maxOdd) maxOdd = idVal;
                        }
                        else
                        {
                            if (idVal > maxEven) maxEven = idVal;
                        }
                    }

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

            fallbackPlayerId = isPlayer1 
                ? (maxOdd == 0 ? 1 : maxOdd + 2).ToString() 
                : (maxEven == 0 ? 2 : maxEven + 2).ToString();

            if (!string.IsNullOrEmpty(existingPairId))
            {
                fallbackPairId = existingPairId;
            }
            else
            {
                fallbackPairId = (maxPair + 1).ToString();
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[PlayerDataSaver] Error calculating fallback IDs: {ex.Message}");
            fallbackPlayerId = isPlayer1 ? "1" : "2";
            fallbackPairId = "1";
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
            playerId = "", // Apps Script yang akan mengisi
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
                        string assignedId = response.playerId > 0 ? response.playerId.ToString() : null;
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
        public int playerId;
        public int pairId;
        public string message;
    }
}
