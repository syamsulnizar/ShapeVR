using TMPro;
using UnityEngine;
using UnityEngine.Events;

public class Timer : MonoBehaviour
{
    public TextMeshPro timeText;
    private bool started = false;
    private float time = 0;
    public UnityEvent OnTimeUp;
    public bool startOnAwake = false;

    private void Start()
    {
        if (startOnAwake)
            StartTimer();
    }

    private void Update()
    {
        if (started)
            time += Time.deltaTime;
    }

    public void StartTimer()
    {
        started = true;
    }

    public void StopTimer()
    {
        started = false;
        OnTimeUp?.Invoke();
        timeText.text = $"{time:0.00}";
    }
}
