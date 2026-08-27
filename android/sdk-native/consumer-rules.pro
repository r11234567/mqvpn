# JNI downcall: NativeBridge native method names (resolved by static JNI names)
-keepclasseswithmembernames class **.NativeBridge { native <methods>; }

# JNI upcall: callbacks resolved by string name through GetMethodID (mqvpn_jni.c:450-466)
-keep class **.TunnelCallbacks { *; }
-keepclassmembers class * implements **.TunnelCallbacks {
    void onNative*(...);
}

# JNI upcall: the Android CA-store trust check, resolved by class and method
# name in JNI_OnLoad. Renaming or removing it does not fail the build — it
# makes every TLS handshake reject the server at runtime.
-keep class com.mqvpn.sdk.native_.PlatformTrust {
    public static java.lang.String checkServerTrusted(byte[][]);
}
