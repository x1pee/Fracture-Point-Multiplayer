namespace FractureTogether;

// Pinned serialized AnimatorController metadata, extracted from
// sharedassets0.assets object 3189 (EnemyAnimator) and sharedassets1.assets
// object 5636 (EnemyAnimator 2), m_Controller.m_Values and m_TOS. Both native
// controllers have the identical table below. Types are Unity's Float=1,
// Int=3, Bool=4. Triggers use layer snapshots.
// Do not enumerate Animator.parameters or GetParameter: this IL2CPP build
// exposes an invalid generic array and an unstripped stub respectively.
internal static class EnemyAnimatorParameters
{
 internal readonly record struct Parameter(uint Hash,int Type);
 internal static readonly Dictionary<string,Parameter[]> Controllers=new(StringComparer.Ordinal)
 {
  ["EnemyAnimator"]=new[]{new Parameter(3205784029u,1),new Parameter(3471299058u,1),new Parameter(1855955664u,3),new Parameter(153482222u,4),new Parameter(2279274030u,4),new Parameter(2983595267u,4),new Parameter(2852464175u,1),new Parameter(1342839628u,1)},
  ["EnemyAnimator 2"]=new[]{new Parameter(3205784029u,1),new Parameter(3471299058u,1),new Parameter(1855955664u,3),new Parameter(153482222u,4),new Parameter(2279274030u,4),new Parameter(2983595267u,4),new Parameter(2852464175u,1),new Parameter(1342839628u,1)},
  ["Military Animator Controller"]=new[]{new Parameter(1784438316u,1)}
 };
}
