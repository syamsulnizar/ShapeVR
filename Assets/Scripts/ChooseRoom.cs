using UnityEngine;
using UnityEngine.Events;

public class ChooseRoom : MonoBehaviour
{
    public UnityEvent isPlay;

    private void Start()
    {
        if (RoomOption.Instance.isPlayed)
        {
            isPlay?.Invoke();
        }
    }

    public void Play()
    {
        RoomOption.Instance.Play();
        isPlay?.Invoke();
    }

    public void Sky()
    {
        RoomOption.Instance.CurrentRoom = RoomOption.Room.Sky;
    }

    public void School()
    {
        RoomOption.Instance.CurrentRoom = RoomOption.Room.School;
    }

    public void LivingRoom()
    {
        RoomOption.Instance.CurrentRoom = RoomOption.Room.LivingRoom;
    }

    public void Office()
    {
        RoomOption.Instance.CurrentRoom = RoomOption.Room.Office;
    }
}
