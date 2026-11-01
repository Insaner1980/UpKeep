@{
    Solution = 'UpKeep.slnx'
    TestProjects = @(
        @{
            Path = 'tests/UpKeep.Tests/UpKeep.Tests.csproj'
            Configuration = 'Debug'
            Properties = @{
                Platform = 'x64'
                # Exclude installed-manager probes and the native Recycle Bin test.
                VSTestTestCaseFilter = 'Category!=LiveReadOnly&FullyQualifiedName!~UpKeep.Tests.NativeRecycleIntegrationTests'
            }
        }
    )
    BuildProjects = @(
        @{
            Path = 'UpKeep.slnx'
            Configuration = 'Debug'
            Properties = @{
                Platform = 'x64'
            }
        }
    )
}
