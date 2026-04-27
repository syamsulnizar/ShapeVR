using UnityEngine;

public class ShapeObject : MonoBehaviour
{
    public bool isShaped = false;

    private void Start()
    {
        isShaped = false;
    }

    public void Shape (bool shape)
    {
        isShaped = shape;
    }
}
