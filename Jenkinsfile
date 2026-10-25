// Jenkins CI/CD pipeline for HambotV2.
//
// Point the Jenkins job at this file ("Pipeline script from SCM") so the pipeline
// stays in sync with the solution structure instead of a job-local script.
//
// Requirements:
//   - the Jenkins agent needs the .NET 10 SDK (10.0.x) on its PATH,
//   - the "SSH Agent" plugin must be installed for the Deploy stage.

pipeline {
    agent any

    environment {
        DOTNET_ROOT = '/opt/dotnet10'
        PATH = "/opt/dotnet10:${env.PATH}"
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        DOTNET_NOLOGO = '1'

        // Auto-deploy target (design §5.1). Jenkins reaches the
        // host over SSH; fill these in once per Jenkins install. The deploy
        // directory is a git clone of the same origin, so the pipeline only
        // needs to name the commit — it never transfers files or secrets.
        DEPLOY_HOST = '172.17.0.1'          // host reachable from the Jenkins container: host IP, the docker bridge gateway (172.17.0.1), or host.docker.internal (requires extra_hosts host-gateway)
        DEPLOY_USER = 'hambot-deploy'
        DEPLOY_DIR = '/opt/hambotv2'
        DEPLOY_SSH_CREDENTIAL = 'hambot-deploy-ssh-key'  // Jenkins "SSH Username with private key" credential ID
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

        stage('Deploy') {
            // Only version tags (C-5: v<major.minor.patch>) are ever deployed;
            // ordinary pushes to master, feature branches, and PR builds stop
            // after Test. A stage also only runs when the previous ones
            // succeeded, so a failed build or test run never deploys anything.
            // Tag-triggered deploys require the Jenkins job to build tag refs
            // (see docs/jenkins-cicd.md §7).
            when {
                anyOf {
                    tag pattern: "v\\d+\\.\\d+\\.\\d+", comparator: "REGEXP"   // multibranch tag builds
                    expression {                                                // plain "Pipeline script from SCM" jobs: the built commit carries a version tag
                        sh(
                            returnStdout: true,
                            script: 'git tag --points-at "$GIT_COMMIT" | grep -x -E "v[0-9]+\\.[0-9]+\\.[0-9]+" || true'
                        ).trim() != ''
                    }
                }
            }
            steps {
                sshagent(credentials: [env.DEPLOY_SSH_CREDENTIAL]) {
                    // Check the server's clone out at exactly the commit CI
                    // built and tested, then run the repo's own deploy script
                    // (build image, replace containers, verify startup,
                    // rollback on a failed health check, prune, record build).
                    // Secrets stay in the server's gitignored .env.
                    sh '''
                        ssh -o StrictHostKeyChecking=accept-new "$DEPLOY_USER@$DEPLOY_HOST" \
                            "cd $DEPLOY_DIR && \
                             git fetch origin && \
                             git checkout --force $GIT_COMMIT && \
                             bash scripts/deploy.sh $GIT_COMMIT $BUILD_NUMBER '$BUILD_URL'"
                    '''
                }
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
