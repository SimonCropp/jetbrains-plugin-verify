import com.jetbrains.plugin.structure.base.utils.isFile
import org.apache.tools.ant.taskdefs.condition.Os
import org.jetbrains.intellij.platform.gradle.Constants

plugins {
    id("java")
    alias(libs.plugins.kotlinJvm)
    id("org.jetbrains.intellij.platform") version "2.18.1"     // See https://github.com/JetBrains/intellij-platform-gradle-plugin/releases
    id("me.filippov.gradle.jvm.wrapper") version "0.15.0"
}

val isWindows = Os.isFamily(Os.FAMILY_WINDOWS)
extra["isWindows"] = isWindows

val DotnetSolution: String by project
val BuildConfiguration: String by project
val ProductVersion: String by project
val DotnetPluginId: String by project
val RiderPluginId: String by project
val PublishToken: String by project

allprojects {
    repositories {
        maven { setUrl("https://cache-redirector.jetbrains.com/maven-central") }
    }
}

repositories {
    intellijPlatform {
        defaultRepositories()
        jetbrainsRuntime()
    }
}

version = extra["PluginVersion"] as String

tasks.processResources {
    from("dependencies.json") { into("META-INF") }
}

sourceSets {
    main {
        java.srcDir("src/rider/main/java")
        kotlin.srcDir("src/rider/main/kotlin")
        resources.srcDir("src/rider/main/resources")
    }
}

// What `dotnet build` and `dotnet pack` are both given: which solution, which configuration, and
// the empty HostFullIdentifier that keeps a ReSharper host out of the plugin build.
//
// The .NET SDK on every OS. Windows used to hunt for Visual Studio's MSBuild with vswhere, which
// was wrong twice over. "-products *" is not the question "where is MSBuild": other Microsoft
// installers register through the same Visual Studio setup API and are returned by it, so SQL
// Server Management Studio 22 answered it - version 22.8.x beats Visual Studio's 17.x under
// -latest - and its cut-down MSBuild failed the build with a bare non-zero exit and nothing else.
// Nor does picking the real Visual Studio help: its MSBuild cannot restore these projects at all,
// skipping every one with NU1503 "the project file may be invalid or missing targets required for
// restore". The SDK is already required here, since this is an SDK solution, and Visual Studio now
// is not.
val dotnetArgs = listOf(
        DotnetSolution,
        "--configuration", BuildConfiguration,
        "-p:HostFullIdentifier=",
        "-v:minimal",
)

val compileDotNet by tasks.registering {
    doLast {
        providers.exec {
            executable("dotnet")
            // `build` rather than msbuild /t:Restore;Rebuild - it restores on the way in, and is
            // incremental where Rebuild always cleaned first. This task has no up-to-date check of
            // its own, so it runs every time either way; incremental just makes the repeats cheap.
            args(listOf("build") + dotnetArgs)
            workingDir(rootDir)
        }.result.get()
    }
}

val testDotNet by tasks.registering {
    doLast {
        providers.exec {
            executable("dotnet")
            args("test","${DotnetSolution}","--logger","GitHubActions")
            workingDir(rootDir)
        }.result.get()
    }
}

tasks.buildPlugin {
    doLast {
        copy {
            from(layout.buildDirectory.file("distributions/${rootProject.name}-${version}.zip"))
            into("${rootDir}/output")
        }

        // TODO: See also org.jetbrains.changelog: https://github.com/JetBrains/gradle-changelog-plugin
        val changelogText = file("${rootDir}/CHANGELOG.md").readText()
        val changelogMatches = Regex("(?s)(-.+?)(?=##|$)").findAll(changelogText)
        val changeNotes = changelogMatches.map {
            it.groups[1]!!.value.replace("(?s)- ".toRegex(), "\u2022 ").replace("`", "").replace(",", "%2C").replace(";", "%3B")
        }.take(1).joinToString()

        providers.exec {
            executable("dotnet")
            args(listOf("pack") + dotnetArgs + listOf(
                    "-p:PackageOutputPath=${rootDir}/output",
                    "-p:PackageReleaseNotes=${changeNotes}",
                    "-p:PackageVersion=${version}",
            ))
            workingDir(rootDir)
        }.result.get()
    }
}

dependencies {
    intellijPlatform {
        rider(ProductVersion) {
            useInstaller = false
        }
        jetbrainsRuntime()
        bundledModule("intellij.rd.client")
        bundledModule("intellij.rider.rdclient.dotnet")

        // TODO: add plugins
        // bundledPlugin("uml")
        // bundledPlugin("com.jetbrains.ChooseRuntime:1.0.9")
    }
}

tasks.runIde {
    // Match Rider's default heap size of 1.5Gb (default for runIde is 512Mb)
    maxHeapSize = "1500m"
}

tasks.patchPluginXml {
    // TODO: See also org.jetbrains.changelog: https://github.com/JetBrains/gradle-changelog-plugin
    val changelogText = file("${rootDir}/CHANGELOG.md").readText()
    val changelogMatches = Regex("(?s)(-.+?)(?=##|\$)").findAll(changelogText)

    changeNotes.set(changelogMatches.map {
        it.groups[1]!!.value.replace("(?s)\r?\n".toRegex(), "<br />\n")
    }.take(1).joinToString())
}

tasks.prepareSandbox {
    dependsOn(compileDotNet)

    val outputFolder = "${rootDir}/src/dotnet/${DotnetPluginId}/bin/${DotnetPluginId}.Rider/${BuildConfiguration}"
    val dllFiles = listOf(
            "$outputFolder/${DotnetPluginId}.dll",
            "$outputFolder/${DotnetPluginId}.pdb",
            "$outputFolder/DiffEngine.dll",
            "$outputFolder/EmptyFiles.dll",
            "$outputFolder/Verify.ExceptionParsing.dll",
            // TODO: add additional assemblies
    )

    dllFiles.forEach { f ->
        val file = file(f)
        from(file) { into("${rootProject.name}/dotnet") }
    }

    doLast {
        dllFiles.forEach { f ->
            val file = file(f)
            if (!file.exists()) throw RuntimeException("File $file does not exist")
        }
    }
}

tasks.publishPlugin {
//     dependsOn testDotNet
    dependsOn(tasks.buildPlugin)
    token.set("${PublishToken}")

    doLast {
        providers.exec {
            executable("dotnet")
            args("nuget","push","output/${DotnetPluginId}.${version}.nupkg","--api-key","${PublishToken}","--source","https://plugins.jetbrains.com")
            workingDir(rootDir)
        }.result.get()
    }
}

val riderModel: Configuration by configurations.creating {
    isCanBeConsumed = true
    isCanBeResolved = false
}

artifacts {
    add(riderModel.name, provider {
        intellijPlatform.platformPath.resolve("lib/rd/rider-model.jar").also {
            check(it.isFile) {
                "rider-model.jar is not found at $riderModel"
            }
        }
    }) {
        builtBy(Constants.Tasks.INITIALIZE_INTELLIJ_PLATFORM_PLUGIN)
    }
}
