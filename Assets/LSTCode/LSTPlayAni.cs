using UnityEngine;

public class LSTPlayAni : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    Animation anim;
    void Start()
    {
        anim = GetComponent<Animation>();
    }

    // Update is called once per frame
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            anim.Play("TraiTimVaPhoi");
        }
    }
}
