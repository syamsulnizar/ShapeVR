using UnityEngine;

public class RoomOption : MonoBehaviour
{
    public static RoomOption Instance { get; private set; }
    public Room CurrentRoom;
    public bool isPlayed = false;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
        }
        else
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
    }

    public void Play()
    {
        isPlayed = true;
    }

    public enum Room
    {
        LivingRoom,
        School,
        Sky,
        Office
    }
}
