using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using Unity.Netcode;

public class PlayerDataSaver : MonoBehaviour
{
    public static PlayerDataSaver Instance { get; private set; }

    [Header("Google Sheets Integration")]
    [Tooltip("URL Web App dari Google Apps Script.")]
    [SerializeField] private string googleSheetsUrl = "";

    [Header("Debug")]
    [SerializeField] private int correctAnswers = 0;

    private float startTime;
    private bool dataSaved = false;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    private void Start()
    {
        startTime = Time.time;
        dataSaved = false;

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

    private void OnGameWon()
    {
        if (dataSaved) return;
        dataSaved = true;

        SavePlayerData();
    }

    private void SavePlayerData()
    {
        // 1. Player Role & Temp ID
        string playerRole = "Player 1";
        var nm = NetworkManager.Singleton;
        if (nm != null && (nm.IsHost || nm.IsClient))
        {
            ulong clientId = nm.LocalClientId;
            playerRole = clientId == 0 ? "Player 1" : $"Player {clientId + 1}";
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

        Debug.Log($"[PlayerDataSaver] Session Completed: Role={playerRole}, PlayTime={playTime}, Location={location}, Duration={completionTime:0.00}s, Correct={correct}, Incorrect={incorrect}");

        // Kirim ke Google Sheets (ini akan menyimpan ke local CSV setelah selesai/gagal)
        SendToGoogleSheets(playerRole, playTime, location, completionTime, correct, incorrect);
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
                        return "Choose Room - Space";
                    case RoomOption.Room.School:
                        return "Choose Room - School";
                    case RoomOption.Room.Office:
                        return "Choose Room - Office";
                    case RoomOption.Room.LivingRoom:
                        return "Choose Room - Living Room";
                }
            }
            return "Virtual Choose Room";
        }

        return sceneName;
    }

    private void SaveLocalCSV(string playerId, string playerRole, string playTime, string location, float completionTime, int correct, int incorrect)
    {
        string header = "PlayerID,PlayerRole,PlayTimeGMT,Location,CompletionTime,CorrectAnswers,IncorrectAnswers";
        string persistentPath = Path.Combine(Application.persistentDataPath, "PlayerData.csv");

        string finalPlayerId = playerId;
        if (string.IsNullOrEmpty(finalPlayerId))
        {
            finalPlayerId = GetNextLocalPlayerIdFromFile(persistentPath).ToString();
        }

        string csvLine = $"{finalPlayerId},{playerRole},{playTime},{location},{completionTime:0.00},{correct},{incorrect}";
        WriteRowToFile(persistentPath, header, csvLine);

#if UNITY_EDITOR
        string projectPath = Path.Combine(Application.dataPath, "PlayerData.csv");
        if (string.IsNullOrEmpty(playerId))
        {
            finalPlayerId = GetNextLocalPlayerIdFromFile(projectPath).ToString();
            csvLine = $"{finalPlayerId},{playerRole},{playTime},{location},{completionTime:0.00},{correct},{incorrect}";
        }
        WriteRowToFile(projectPath, header, csvLine);
#endif
    }

    private int GetNextLocalPlayerIdFromFile(string filePath)
    {
        if (!File.Exists(filePath)) return 1;
        try
        {
            string[] lines = File.ReadAllLines(filePath);
            if (lines.Length > 1)
            {
                string lastLine = lines[lines.Length - 1];
                string[] parts = lastLine.Split(',');
                if (parts.Length > 0 && int.TryParse(parts[0], out int lastId))
                {
                    return lastId + 1;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[PlayerDataSaver] Error reading last ID from {filePath}: {ex.Message}");
        }
        return 1;
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

    private void SendToGoogleSheets(string playerRole, string playTime, string location, float completionTime, int correct, int incorrect)
    {
        PlayerDataPayload payload = new PlayerDataPayload
        {
            playerId = "", // Biarkan kosong, Apps Script yang akan mengisi
            playerRole = playerRole,
            playTime = playTime,
            location = location,
            completionTime = completionTime,
            correctAnswers = correct,
            incorrectAnswers = incorrect
        };

        string json = JsonUtility.ToJson(payload);
        StartCoroutine(PostRequest(json, playerRole, playTime, location, completionTime, correct, incorrect));
    }

    private IEnumerator PostRequest(string json, string playerRole, string playTime, string location, float completionTime, int correct, int incorrect)
    {
        if (string.IsNullOrEmpty(googleSheetsUrl))
        {
            Debug.Log("[PlayerDataSaver] Google Sheets URL is not configured. Saving local CSV only.");
            SaveLocalCSV(null, playerRole, playTime, location, completionTime, correct, incorrect);
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
                
                // Parse response untuk mendapatkan ID yang diberikan oleh Google Sheets
                try
                {
                    GoogleSheetsResponse response = JsonUtility.FromJson<GoogleSheetsResponse>(request.downloadHandler.text);
                    if (response != null && response.result == "success")
                    {
                        string assignedId = response.playerId > 0 ? response.playerId.ToString() : null;
                        SaveLocalCSV(assignedId, playerRole, playTime, location, completionTime, correct, incorrect);
                    }
                    else
                    {
                        SaveLocalCSV(null, playerRole, playTime, location, completionTime, correct, incorrect);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[PlayerDataSaver] Failed to parse Google Sheets response ID: " + ex.Message);
                    SaveLocalCSV(null, playerRole, playTime, location, completionTime, correct, incorrect);
                }
            }
            else
            {
                Debug.LogWarning("[PlayerDataSaver] Failed to upload data to Google Sheets: " + request.error + ". Saving locally.");
                SaveLocalCSV(null, playerRole, playTime, location, completionTime, correct, incorrect);
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
    }

    [Serializable]
    private class GoogleSheetsResponse
    {
        public string result;
        public int playerId;
        public string message;
    }
}
