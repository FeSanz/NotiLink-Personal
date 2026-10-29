import java.text.SimpleDateFormat
import java.util.Date

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.notilink.app"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.notilink.app"
        minSdk = 26
        targetSdk = 35
        versionCode = 6
        // Kept in sync by hand with windows/NotiLink.App/NotiLink.App.csproj's <Version> —
        // see the "Acerca de" section on both apps.
        versionName = "1.5.0"

        // Computed at build time (not a file timestamp) so it's accurate for every build,
        // including a Release build baked into an APK someone installs later — read at
        // runtime via BuildConfig.BUILD_TIMESTAMP in MainActivity's "Acerca de" section.
        buildConfigField("String", "BUILD_TIMESTAMP", "\"${SimpleDateFormat("yyyy-MM-dd HH:mm").format(Date())}\"")
    }

    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    buildFeatures {
        viewBinding = true
        buildConfig = true
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("com.google.android.material:material:1.12.0")
    implementation("androidx.lifecycle:lifecycle-service:2.8.6")
    implementation("androidx.recyclerview:recyclerview:1.3.2")
    implementation("androidx.security:security-crypto:1.1.0-alpha06")
    implementation("com.journeyapps:zxing-android-embedded:4.3.0")
}
