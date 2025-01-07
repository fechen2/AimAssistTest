using System.Collections.Generic;
using Cinemachine.Utility;
using UnityEngine;

public class PlayerMoveTest : MonoBehaviour
{
    public float Speed;
    public float VelocityDamping;
    [SerializeField] private GunAimRightStickAssist m_gunAimRightStickAssist;
    public GunAimRightStickAssist GunAimRightStickAssist => m_gunAimRightStickAssist;
    Vector3 m_currentVelocity;

    private void Reset()
    {
        Speed = 5;
        VelocityDamping = 0.5f;
        m_currentVelocity = Vector3.zero;
    }

    void Update()
    {
#if ENABLE_LEGACY_INPUT_MANAGER
        Vector3 fwd = Camera.main.transform.forward;
        fwd.y = 0;
        fwd = fwd.normalized;
        if (fwd.sqrMagnitude < 0.01f)
            return;

        Quaternion inputFrame = Quaternion.LookRotation(fwd, Vector3.up);
        Vector3 leftStickMovement = new Vector3(Input.GetAxis("Horizontal"), 0, Input.GetAxis("Vertical"));
        leftStickMovement = inputFrame * leftStickMovement;

        var dt = Time.deltaTime;
        var desiredVelocity = leftStickMovement * Speed;
        var deltaVel = desiredVelocity - m_currentVelocity;
        m_currentVelocity += Damper.Damp(deltaVel, VelocityDamping, dt);

        //xbox controller right stick
        Vector2 rightStickAimDirection = new Vector3(Input.GetAxis("AimX"), Input.GetAxis("AimY"));

        // if (leftStickMovement.magnitude > 0)
        // {
        //     Debug.Log($"leftStickMovement: {leftStickMovement}");
        // }
        //
        // if (rightStickAimDirection.magnitude > 0)
        // {
        //     Debug.Log($"rightStickAimDirection: {rightStickAimDirection}");
        // }

        SetPosition(dt);
        SetRotation(dt, rightStickAimDirection);
#else
        InputSystemHelper.EnableBackendsWarningMessage();
#endif
    }

    private void SetPosition(float dt)
    {
        transform.position += m_currentVelocity * dt;
    }

    private Target[] _targets;
    private void SetRotation(float dt, Vector2 rightStickAimDirection)
    {
        if (rightStickAimDirection.magnitude < float.Epsilon)
        {
            if (m_currentVelocity.magnitude < float.Epsilon)
            {
                return;
            }

            //use left stick direction to rotate player
            var qA = transform.rotation;
            var qB = Quaternion.LookRotation(m_currentVelocity);
            transform.rotation = Quaternion.Slerp(qA, qB, Damper.Damp(1, VelocityDamping, dt));
        }
        else
        {
            if (_targets == null || _targets.Length == 0)
            {
                _targets = GameObject.FindObjectsOfType<Target>();
            }
            
            m_gunAimRightStickAssist.SetTargets(_targets);

            (bool isValid, Vector3 assistResult) = m_gunAimRightStickAssist.GetDirection(
                new Vector3(rightStickAimDirection.x, 0, rightStickAimDirection.y),
                transform.position);

            if (isValid)
            {
                transform.rotation = Quaternion.LookRotation(assistResult.normalized, Vector3.up);
            }
            else
            {
                transform.rotation = Quaternion.LookRotation(new Vector3(rightStickAimDirection.x, 0, rightStickAimDirection.y));
            }
        }
    }
}
