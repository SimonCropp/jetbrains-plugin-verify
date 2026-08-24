pluginManagement {
    // Provide repositories to resolve plugins
    repositories {
        maven { setUrl("https://cache-redirector.jetbrains.com/plugins.gradle.org") }
        maven { setUrl("https://cache-redirector.jetbrains.com/maven-central") }
        maven { setUrl("https://cache-redirector.jetbrains.com/dl.bintray.com/kotlin/kotlin-eap") }
    }
    resolutionStrategy {
        eachPlugin {
            // Gradle has to map a plugin dependency to Maven coordinates - '{groupId}:{artifactId}:{version}'. It tries
            // to do use '{plugin.id}:{plugin.id}.gradle.plugin:version'.
            // This doesn't work for rdgen, so we provide some help
            if (requested.id.id == "com.jetbrains.rdgen") {
                useModule("com.jetbrains.rd:rd-gen:${requested.version}")
            }
        }
    }
}

// Lets Gradle fetch the JDK the build asks for instead of failing when the machine has none.
// The IntelliJ Platform plugin sets the toolchain from the target IDE - Rider 2026.2 wants Java
// 25 - and Gradle will not go looking for one without a resolver, so a machine with no JDK 25
// installed fails at :compileKotlin with "Toolchain download repositories have not been
// configured". Nothing else here provides one: the JetBrains Runtime this project depends on is
// resolved as a dependency and is not visible to toolchain detection, and gradlew's own JVM is
// whatever launched it.
plugins {
    id("org.gradle.toolchains.foojay-resolver-convention") version "1.0.0"
}

rootProject.name = "ReSharperPlugin.Verify"

include(":protocol")
