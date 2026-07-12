# Native bridge smoke executable

This target compiles the real Windows Named Pipe implementation without UE4SS. It proves the native framing, ACL, hello, heartbeat and shutdown behavior against Control API.

The UE4SS `dllmain.cpp` remains a thin official-template adapter around this tested component.
