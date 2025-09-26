using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class PlayerController : MonoBehaviour
{
    public float moveSpeed = 6f;
    public float jumpSpeed = 6f;
    public float gravity = -20f;
    public Transform cam;

    CharacterController cc;
    float vy;

    void Awake() { cc = GetComponent<CharacterController>(); }

    void Update()
    {
        // mouse look
        float mx = Input.GetAxis("Mouse X");
        float my = Input.GetAxis("Mouse Y");
        transform.Rotate(0f, mx * 3f, 0f);
        Vector3 angles = cam.localEulerAngles;
        float pitch = angles.x; if (pitch > 180f) pitch -= 360f;
        pitch = Mathf.Clamp(pitch - my * 3f, -89f, 89f);
        cam.localEulerAngles = new Vector3(pitch, 0f, 0f);

        // move
        Vector3 input = new Vector3(Input.GetAxisRaw("Horizontal"), 0f, Input.GetAxisRaw("Vertical"));
        input = Vector3.ClampMagnitude(input, 1f);
        Vector3 world = transform.TransformDirection(input) * moveSpeed;

        // jump
        if (cc.isGrounded)
        {
            vy = -1f;
            if (Input.GetButtonDown("Jump")) vy = jumpSpeed;
        }
        else
        {
            vy += gravity * Time.deltaTime;
        }

        world.y = vy;
        cc.Move(world * Time.deltaTime);
    }
}
