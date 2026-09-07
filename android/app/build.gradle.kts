plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.plugin.compose")
    id("com.google.dagger.hilt.android")
    id("com.google.devtools.ksp")
}

android {
    namespace = "com.mqvpn.app"
    compileSdk = 37

    // AGP embeds a Google-signed "dependency metadata" block in the APK
    // signing block by default. It is not reproducible and F-Droid rejects
    // it (its APK scanner flags an extra signing block), so keep it out.
    dependenciesInfo {
        includeInApk = false
        includeInBundle = false
    }

    defaultConfig {
        applicationId = "org.mqvpn.app"
        minSdk = 26
        targetSdk = 36
        versionCode = 41
        versionName = "0.16.4"
        // arm64-v8a only: must match sdk-native's abiFilters. Adding ABIs here
        // without updating sdk-native produces APKs that crash with
        // UnsatisfiedLinkError on those ABIs (no .so packaged).
        ndk {
            abiFilters += listOf("arm64-v8a")
        }
    }

    // This public repository key gives every release the same Android app
    // identity without depending on CI secrets. It is intentionally not a
    // Play upload key and provides continuity, not publisher authentication.
    signingConfigs {
        create("release") {
            storeFile = file("signing/mqvpn-release.p12")
            storePassword = "mqvpn-release"
            storeType = "PKCS12"
            keyAlias = "mqvpn-release"
            keyPassword = "mqvpn-release"
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            if (!providers.gradleProperty("mqvpn.disableReleaseSigning").isPresent) {
                signingConfig = signingConfigs.getByName("release")
            }
        }
    }

    buildFeatures {
        compose = true
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_11
        targetCompatibility = JavaVersion.VERSION_11
    }
}

dependencies {
    implementation(project(":sdk-core"))

    // Compose
    implementation(platform("androidx.compose:compose-bom:2026.06.01"))
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.material:material-icons-extended")
    implementation("androidx.activity:activity-compose:1.13.0")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.11.0")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.11.0")

    // Hilt
    implementation("com.google.dagger:hilt-android:2.60.1")
    ksp("com.google.dagger:hilt-android-compiler:2.60.1")
    implementation("androidx.hilt:hilt-navigation-compose:1.4.0")
    implementation("androidx.navigation:navigation-compose:2.9.8")

    // DataStore
    implementation("androidx.datastore:datastore-preferences:1.2.1")

    // Test
    testImplementation("junit:junit:4.13.2")
    testImplementation("io.mockk:mockk:1.14.11")
    testImplementation("org.jetbrains.kotlinx:kotlinx-coroutines-test:1.11.0")
}
