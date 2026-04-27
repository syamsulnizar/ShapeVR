using System.Linq;
using UnityEngine;
using UnityEngine.Events;

public class GameManager : MonoBehaviour
{
    public UnityEvent Won;
    public ShapeObject[] shapeObjects;

    public void CheckCondition()
    {
        if(shapeObjects.All(shape => shape.isShaped))
        {
            Won?.Invoke();
        }
    }
}
