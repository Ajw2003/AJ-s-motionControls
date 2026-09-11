using UnityEngine;

// D2: moved out of SkipPhysics.cs (RockSkipping.Physics assembly, which must stay pure and
// dependency-free — see that file's class remarks). This is a MonoBehaviour, so it belongs in
// Assembly-CSharp instead. Fields and comments kept verbatim from 9c3f201.
public class FakeSkip : MonoBehaviour
{
    public float energyloss = 0.2f;
    public float Weight = 1f;
    public float Thickness = 0.2f;
    public float minliftforskip;

    //heavyer stone goes further with more effort but is harder to control

    //lighter stone requires less effort and too much can make it fail



}
