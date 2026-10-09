# Tests

NUnit suites for the Unity Test Runner. They cover the parts of the SDK that need the
Unity player loop or a real `MonoBehaviour`: the manager's lifecycle, transports,
ownership, the room flow, the crypto layer and the Editor windows.

## Running the tests

Unity builds a package's tests only when the package is listed as testable. Add it once
to `Packages/manifest.json`:

```json
{
  "testables": [ "com.rtmpe.sdk" ]
}
```

Then open **Window → General → Test Runner**; the suites appear under **EditMode** and
**PlayMode**.

The three assembly definitions in this folder carry
`"defineConstraints": ["UNITY_INCLUDE_TESTS"]`. Unity sets that symbol for test builds
only, so none of this code is compiled into a player build.

The performance suite in `Tests/Runtime/Performance/` also requires the
`com.unity.test-framework.performance` package, version 3.0.0 or later. It stays hidden
until you add that package to your project.

## Scope

Use these suites to exercise the SDK's Unity-specific behaviour in your own Editor, on
your Unity version and with your project settings. A failure here usually points to an
interaction between the package and the project's configuration.
