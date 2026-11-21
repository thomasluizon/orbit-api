# Persisted broadcast invocation

`LegacyBroadcastPage.json` is a job payload that Hangfire 1.8.25 serialized. It comes from the page job and command sources at commit `38d2d79505b9836d57b84efc0c0defb700110a63`.

The payload uses the `Version_180` compatibility level, simple assembly type names and the recommended serializer settings. The transition shim did not produce it.

The recipient ids, the content and the expiry date are synthetic inputs to the real producer. The regression test reads the payload with the real Hangfire deserializer.

To reproduce the file, build the job from the sources at that commit in a temporary console project, with the same compatibility level. Write the result of `InvocationData.SerializeJob(job).SerializePayload()` to the fixture path.
