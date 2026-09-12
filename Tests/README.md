# Tests

NUnit suites for the Unity Test Runner, covering the parts of the SDK that need
a Unity player loop or a real `MonoBehaviour` to exercise: the manager's
lifecycle, transports, ownership, room flow, the crypto layer and the Editor
surfaces.

## Running them

They are outside your project's compilation by default. Unity only builds a
package's tests when the package is listed in `testables`, so add it once in
`Packages/manifest.json`:

```json
{
  "testables": [ "com.rtmpe.sdk" ]
}
```

Then **Window → General → Test Runner**; the suites appear under `EditMode` and
`PlayMode`. All three assembly definitions here carry
`"defineConstraints": ["UNITY_INCLUDE_TESTS"]`, which Unity sets for a test
build and clears for a player build — so nothing in this directory reaches a
shipped game whether or not you run it.

⚠️ The third, `Tests/Runtime/Performance/`, additionally requires
`RTMPE_PERFORMANCE_TESTING`, which a versionDefine supplies only when
`com.unity.test-framework.performance` is in your project. This package does not
depend on it, so that suite stays invisible until you add the package yourself.

## What they are not

They are not the SDK's release gate. The engine's own coverage runs outside
Unity, against the same runtime sources, and is what a release is cut on; these
suites exist so that an integrator can exercise the Unity-only surface inside
their own editor, on their own Unity version, against the project settings they
actually ship with.

Read a failure here as a question about the pair — this package and your
project's configuration — rather than as a defect report on its own.
