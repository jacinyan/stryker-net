using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stryker.Abstractions;
using Stryker.Abstractions.Exceptions;
using Stryker.Abstractions.Options;
using Stryker.Abstractions.ProjectComponents;
using Stryker.Abstractions.Reporting;
using Stryker.Abstractions.Testing;
using Stryker.Core.CoverageAnalysis;
using Stryker.TestRunner.Tests;
using Stryker.Utilities.Buildalyzer;
using Stryker.Utilities.Logging;

namespace Stryker.Core.MutationTest;

public interface IMutationTestProcess
{
    MutationTestInput Input { get; }
    void Mutate();
    StrykerRunResult Test(IEnumerable<IMutant> mutantsToTest);
    void Restore();
    void GetCoverage();
    void FilterMutants();
}

public class MutationTestProcess : IMutationTestProcess
{
    private static readonly ILogger Logger = ApplicationLogging.LoggerFactory.CreateLogger<MutationTestProcess>();
    private readonly IReadOnlyProjectComponent _projectContents;
    private readonly IMutationTestExecutor _mutationTestExecutor;
    private readonly IReporter _reporter;
    private readonly ICoverageAnalyser _coverageAnalyser;
    private readonly IStrykerOptions _options;
    private readonly IMutationProcess _mutationProcess;
    private static readonly Dictionary<Language, Func<IStrykerOptions, IMutationProcess>> LanguageMap = [];

    static MutationTestProcess() => DeclareMutationProcessForLanguage<CsharpMutationProcess>(Language.Csharp);

    public static void DeclareMutationProcessForLanguage<T>(Language language) where T : IMutationProcess
    {
        var constructor = typeof(T).GetConstructor([typeof(IStrykerOptions)]);
        if (constructor == null)
        {
            throw new NotSupportedException(
                $"Failed to find a constructor with the appropriate signature for type {typeof(T)}");
        }

        LanguageMap[language] = y => (IMutationProcess)constructor.Invoke([y]);
    }

    public MutationTestProcess(MutationTestInput input,
        IStrykerOptions options,
        IReporter reporter,
        IMutationTestExecutor executor,
        IMutationProcess mutationProcess = null,
        ICoverageAnalyser coverageAnalyzer = null)
    {
        Input = input;
        _reporter = reporter;
        _options = options;
        _mutationTestExecutor = executor;
        _mutationProcess = mutationProcess ?? BuildMutationProcess();
        _coverageAnalyser = coverageAnalyzer ?? new CoverageAnalyser(_options);
        _projectContents = input.SourceProjectInfo.ProjectContents;
    }

    public MutationTestInput Input { get; }

    private IMutationProcess BuildMutationProcess()
    {
        if (LanguageMap.ContainsKey(Input.SourceProjectInfo.AnalyzerResult.GetLanguage()))
        {
            return LanguageMap[Input.SourceProjectInfo.AnalyzerResult.GetLanguage()](_options);
        }

        throw new GeneralStrykerException("no valid language detected || no valid csproj or fsproj was given.");
    }

    public void Mutate()
    {
        Input.TestProjectsInfo.BackupOriginalAssembly(Input.SourceProjectInfo.AnalyzerResult);
        _mutationProcess.Mutate(Input);
    }

    public void FilterMutants() => _mutationProcess.FilterMutants(Input);

    public StrykerRunResult Test(IEnumerable<IMutant> mutantsToTest)
    {
        if (!MutantsToTest(mutantsToTest))
        {
            return new StrykerRunResult(_options, double.NaN);
        }

        TestMutants(mutantsToTest);

        return new StrykerRunResult(_options, _projectContents.GetMutationScore());
    }

    public void Restore() => Input.TestProjectsInfo.RestoreOriginalAssembly(Input.SourceProjectInfo.AnalyzerResult);

    private void TestMutants(IEnumerable<IMutant> mutantsToTest)
    {
        var mutantGroups = BuildMutantGroupsForTest(mutantsToTest.ToList());

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = _options.Concurrency };

        Parallel.ForEach(mutantGroups, parallelOptions, mutants =>
        {
            var reportedMutants = new HashSet<IMutant>();

            _mutationTestExecutor.Test(Input.SourceProjectInfo, mutants,
                Input.InitialTestRun.TimeoutValueCalculator,
                (testedMutants, tests, ranTests, outTests) =>
                    TestUpdateHandler(testedMutants, tests, ranTests, outTests, reportedMutants));

            OnMutantsTested(mutants, reportedMutants);
        });
    }

    private bool TestUpdateHandler(IEnumerable<IMutant> testedMutants, ITestIdentifiers failedTests, ITestIdentifiers ranTests,
        ITestIdentifiers timedOutTest, ISet<IMutant> reportedMutants)
    {
        var testsFailingInitially = Input.InitialTestRun.Result.FailingTests.GetIdentifiers().ToHashSet();
        var continueTestRun = _options.OptimizationMode.HasFlag(OptimizationModes.DisableBail);
        if (testsFailingInitially.Count > 0 && failedTests.GetIdentifiers().Any(id => testsFailingInitially.Contains(id)))
        {
            // some of the failing tests where failing without any mutation
            // we discard those tests
            failedTests = new TestIdentifierList(
                failedTests.GetIdentifiers().Where(t => !testsFailingInitially.Contains(t)));
        }

        foreach (var mutant in testedMutants)
        {
            mutant.AnalyzeTestRun(failedTests, ranTests, timedOutTest, false);

            if (mutant.ResultStatus == MutantStatus.Pending)
            {
                continueTestRun = true; // Not all mutants in this group were tested so we continue
            }

            OnMutantTested(mutant, reportedMutants); // Report on mutant that has been tested
        }

        return continueTestRun;
    }

    private void OnMutantsTested(IEnumerable<IMutant> mutants, ISet<IMutant> reportedMutants)
    {
        foreach (var mutant in mutants)
        {
            if (mutant.ResultStatus == MutantStatus.Pending)
            {
                Logger.LogWarning("Mutation {Id} was not fully tested.", mutant.Id);
            }

            OnMutantTested(mutant, reportedMutants);
        }
    }

    private void OnMutantTested(IMutant mutant, ISet<IMutant> reportedMutants)
    {
        if (mutant.ResultStatus == MutantStatus.Pending || reportedMutants.Contains(mutant))
        {
            // skip duplicates or useless notifications
            return;
        }

        _reporter?.OnMutantTested(mutant);
        reportedMutants.Add(mutant);
    }

    private static bool MutantsToTest(IEnumerable<IMutant> mutantsToTest)
    {
        if (!mutantsToTest.Any())
        {
            return false;
        }

        if (mutantsToTest.Any(x => x.ResultStatus != MutantStatus.Pending))
        {
            throw new GeneralStrykerException(
                "Only mutants to run should be passed to the mutation test process. If you see this message please report an issue.");
        }

        return true;
    }

    private IEnumerable<List<IMutant>> BuildMutantGroupsForTest(IReadOnlyCollection<IMutant> mutantsNotRun)
    {
        if (_options.OptimizationMode.HasFlag(OptimizationModes.DisableMixMutants) ||
            !_options.OptimizationMode.HasFlag(OptimizationModes.CoverageBasedTest))
        {
            return mutantsNotRun.Select(x => new List<IMutant> { x });
        }

        var blocks = new List<List<IMutant>>(mutantsNotRun.Count);
        var mutantsToGroup = mutantsNotRun.ToList();
        // we deal with mutants needing full testing first
        blocks.AddRange(mutantsToGroup.Where(m => m.AssessingTests.IsEveryTest)
            .Select(m => new List<IMutant> { m }));
        mutantsToGroup.RemoveAll(m => m.AssessingTests.IsEveryTest);

        mutantsToGroup = mutantsToGroup.Where(m => m.ResultStatus == MutantStatus.Pending).ToList();

        var testsCount = Input.InitialTestRun.Result.ExecutedTests.Count;
        mutantsToGroup = mutantsToGroup.OrderBy(m => m.AssessingTests.Count).ToList();
        while (mutantsToGroup.Count > 0)
        {
            // we pick the first mutant
            var usedTests = mutantsToGroup[0].AssessingTests;
            var nextBlock = new List<IMutant> { mutantsToGroup[0] };
            mutantsToGroup.RemoveAt(0);
            for (var j = 0; j < mutantsToGroup.Count; j++)
            {
                var currentMutant = mutantsToGroup[j];
                var nextSet = currentMutant.AssessingTests;
                if (nextSet.Count + usedTests.Count > testsCount)
                {
                    break;
                }

                if (nextSet.ContainsAny(usedTests))
                {
                    continue;
                }

                // add this mutant to the block
                nextBlock.Add(currentMutant);
                // remove the mutant from the list of mutants to group
                mutantsToGroup.RemoveAt(j--);
                // add this mutant's tests
                usedTests = usedTests.Merge(nextSet);
            }

            blocks.Add(nextBlock);
        }

        if (mutantsNotRun.Count > blocks.Count)
        {
            Logger.LogDebug(
                "Mutations will be tested in {BlocksCount} test runs, instead of {MutantsNotRun}.",
                blocks.Count,
                mutantsNotRun.Count);
        }
        else
        {
            Logger.LogDebug(
                "Mutations will be tested in {BlocksCount} test runs.",
                blocks.Count);
        }


        return blocks;
    }

    public void GetCoverage() => _coverageAnalyser.DetermineTestCoverage(Input.SourceProjectInfo,
        _mutationTestExecutor.TestRunner, _projectContents.Mutants, Input.InitialTestRun.Result.FailingTests);
}
