using UnityEngine;

public class RoomManager : MonoBehaviour
{
    private RoomOption roomOption;

    public GameObject livingRoom;
    public GameObject school;
    public GameObject sky;
    public GameObject office;

    private void Start()
    {
        roomOption = RoomOption.Instance;

        if(RoomOption.Instance.CurrentRoom == RoomOption.Room.LivingRoom)
        {
            livingRoom.SetActive(true);
            school.SetActive(false);
            sky.SetActive(false);
            office.SetActive(false);
        }
        else if(RoomOption.Instance.CurrentRoom == RoomOption.Room.School)
        {
            livingRoom.SetActive(false);
            school.SetActive(true);
            sky.SetActive(false);
            office.SetActive(false);
        }
        else if(RoomOption.Instance.CurrentRoom == RoomOption.Room.Sky)
        {
            livingRoom.SetActive(false);
            school.SetActive(false);
            sky.SetActive(true);
            office.SetActive(false);
        }
        else if(RoomOption.Instance.CurrentRoom == RoomOption.Room.Office)
        {
            livingRoom.SetActive(false);
            school.SetActive(false);
            sky.SetActive(false);
            office.SetActive(true);
        }
    }

}
