// Jenkins CI pipeline for HambotV2.
//
// Point the Jenkins job at this file ("Pipeline script from SCM") so the pipeline
// stays in sync with the solution structure instead of a job-local script.
//
// Requirement: the Jenkins agent needs the .NET 10 SDK (10.0.x) on its PATH.

pipeline {
    agent any

    environment {
        DOTNET_ROOT = '/opt/dotnet10'
        PATH = "/opt/dotnet10:${env.PATH}"
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        DOTNET_NOLOGO = '1'
    }

    options {
        // Log lines get a timestamp so gateway reconnects etc. are traceable.
        timestamps()
        timeout(time: 30, unit: 'MINUTES')
        disableConcurrentBuilds()
    }

    stages {
        stage('Build') {
            steps {
                // The build is warning-free; this keeps it that way by
                // failing the build on any (new) nullable warning.
                sh 'dotnet build HambotV2.sln --configuration Release /warnaserror:nullable'
            }
        }

        stage('Test') {
            steps {
                sh 'dotnet test HambotV2.sln --configuration Release --no-build --logger "trx;LogFileName=test-results.trx" --results-directory TestResults'
            }
        }
    }

    post {
        always {
            // Publish TRX files so results are inspectable from the Jenkins UI
            // (visible with the "Publish xUnit / TRX test result report" or
            // "Publish Test results" plugins; harmless without them).
            archiveArtifacts artifacts: 'TestResults/**/*.trx', allowEmptyArchive: true
        }

        cleanup {
            cleanWs notFailBuild: true
        }
    }
}
