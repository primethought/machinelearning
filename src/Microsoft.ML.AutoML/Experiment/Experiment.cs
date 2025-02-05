// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.ML.Data;
using Microsoft.ML.Runtime;

namespace Microsoft.ML.AutoML
{
    internal class Experiment<TRunDetail, TMetrics> where TRunDetail : RunDetail
    {
        private readonly MLContext _context;
        private readonly OptimizingMetricInfo _optimizingMetricInfo;
        private readonly TaskKind _task;
        private readonly IProgress<TRunDetail> _progressCallback;
        private readonly ExperimentSettings _experimentSettings;
        private readonly IMetricsAgent<TMetrics> _metricsAgent;
        private readonly IEnumerable<TrainerName> _trainerAllowList;
        private readonly DirectoryInfo _modelDirectory;
        private readonly DatasetColumnInfo[] _datasetColumnInfo;
        private readonly IRunner<TRunDetail> _runner;
        private readonly IList<SuggestedPipelineRunDetail> _history;
        private readonly IChannel _logger;

        private const string _operationCancelledMessage = "OperationCanceledException has been caught after maximum experiment time" +
                        "was reached, and the running MLContext was stopped. Details: {0}";

        private Timer _maxExperimentTimeTimer;
        private Timer _mainContextCanceledTimer;
        private bool _experimentTimerExpired;
        private MLContext _currentModelMLContext;
        private Random _newContextSeedGenerator;

        public Experiment(MLContext context,
            TaskKind task,
            OptimizingMetricInfo metricInfo,
            IProgress<TRunDetail> progressCallback,
            ExperimentSettings experimentSettings,
            IMetricsAgent<TMetrics> metricsAgent,
            IEnumerable<TrainerName> trainerAllowList,
            DatasetColumnInfo[] datasetColumnInfo,
            IRunner<TRunDetail> runner,
            IChannel logger)
        {
            _context = context;
            _history = new List<SuggestedPipelineRunDetail>();
            _optimizingMetricInfo = metricInfo;
            _task = task;
            _progressCallback = progressCallback;
            _experimentSettings = experimentSettings;
            _metricsAgent = metricsAgent;
            _trainerAllowList = trainerAllowList;
            _modelDirectory = GetModelDirectory(_experimentSettings.CacheDirectory);
            _datasetColumnInfo = datasetColumnInfo;
            _runner = runner;
            _logger = logger;
            _experimentTimerExpired = false;
        }

        private void MaxExperimentTimeExpiredEvent(object state)
        {
            // If at least one model was run, end experiment immediately.
            // Else, wait for first model to run before experiment is concluded.
            _experimentTimerExpired = true;
            if (_history.Any(r => r.RunSucceeded))
            {
                _logger.Warning("Allocated time for Experiment of {0} seconds has elapsed with {1} models run. Ending experiment...",
                    _experimentSettings.MaxExperimentTimeInSeconds, _history.Count());
                _currentModelMLContext.CancelExecution();
            }
        }

        private void MainContextCanceledEvent(object state)
        {
            // If the main MLContext is canceled, cancel the ongoing model training and MLContext.
            if ((_context.Model.GetEnvironment() as ICancelable).IsCanceled)
            {
                _logger.Warning("Main MLContext has been canceled. Ending experiment...");
                // Stop timer to prevent restarting and prevent continuous calls to
                // MainContextCanceledEvent
                _mainContextCanceledTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _currentModelMLContext.CancelExecution();
            }
        }

        private void RelayCurrentContextLogsToLogger(object sender, LoggingEventArgs e)
        {
            // Relay logs that are generated by the current MLContext to the Experiment class's
            // _logger.
            switch (e.Kind)
            {
                case ChannelMessageKind.Trace:
                    _logger.Trace(e.Message);
                    break;
                case ChannelMessageKind.Info:
                    _logger.Info(e.Message);
                    break;
                case ChannelMessageKind.Warning:
                    _logger.Warning(e.Message);
                    break;
                case ChannelMessageKind.Error:
                    _logger.Error(e.Message);
                    break;
                default:
                    throw new NotImplementedException($"{nameof(ChannelMessageKind)}.{e.Kind} is not yet implemented.");
            }
        }

        public IList<TRunDetail> Execute()
        {
            var iterationResults = new List<TRunDetail>();
            // Create a timer for the max duration of experiment. When given time has
            // elapsed, MaxExperimentTimeExpiredEvent is called to interrupt training
            // of current model. Timer is not used if no experiment time is given, or
            // is not a positive number.
            if (_experimentSettings.MaxExperimentTimeInSeconds > 0)
            {
                _maxExperimentTimeTimer = new Timer(
                    new TimerCallback(MaxExperimentTimeExpiredEvent), null,
                    _experimentSettings.MaxExperimentTimeInSeconds * 1000, Timeout.Infinite
                );
            }
            // If given max duration of experiment is 0, only 1 model will be trained.
            // _experimentSettings.MaxExperimentTimeInSeconds is of type uint, it is
            // either 0 or >0.
            else
                _experimentTimerExpired = true;

            // Add second timer to check for the cancelation signal from the main MLContext
            // to the active child MLContext. This timer will propagate the cancelation
            // signal from the main to the child MLContexs if the main MLContext is
            // canceled.
            _mainContextCanceledTimer = new Timer(new TimerCallback(MainContextCanceledEvent), null, 1000, 1000);

            // Pseudo random number generator to result in deterministic runs with the provided main MLContext's seed and to
            // maintain variability between training iterations.
            int? mainContextSeed = ((ISeededEnvironment)_context.Model.GetEnvironment()).Seed;
            _newContextSeedGenerator = (mainContextSeed.HasValue) ? RandomUtils.Create(mainContextSeed.Value) : null;

            do
            {
                try
                {
                    var iterationStopwatch = Stopwatch.StartNew();

                    // get next pipeline
                    var getPipelineStopwatch = Stopwatch.StartNew();

                    // A new MLContext is needed per model run. When max experiment time is reached, each used
                    // context is canceled to stop further model training. The cancellation of the main MLContext
                    // a user has instantiated is not desirable, thus additional MLContexts are used.
                    _currentModelMLContext = _newContextSeedGenerator == null ? new MLContext() : new MLContext(_newContextSeedGenerator.Next());
                    _currentModelMLContext.Log += RelayCurrentContextLogsToLogger;
                    var pipeline = PipelineSuggester.GetNextInferredPipeline(_currentModelMLContext, _history, _datasetColumnInfo, _task,
                        _optimizingMetricInfo.IsMaximizing, _experimentSettings.CacheBeforeTrainer, _logger, _trainerAllowList);
                    // break if no candidates returned, means no valid pipeline available
                    if (pipeline == null)
                    {
                        break;
                    }

                    // evaluate pipeline
                    _logger.Trace($"Evaluating pipeline {pipeline.ToString()}");
                    (SuggestedPipelineRunDetail suggestedPipelineRunDetail, TRunDetail runDetail)
                        = _runner.Run(pipeline, _modelDirectory, _history.Count + 1);

                    _history.Add(suggestedPipelineRunDetail);
                    WriteIterationLog(pipeline, suggestedPipelineRunDetail, iterationStopwatch);

                    runDetail.RuntimeInSeconds = iterationStopwatch.Elapsed.TotalSeconds;
                    runDetail.PipelineInferenceTimeInSeconds = getPipelineStopwatch.Elapsed.TotalSeconds;

                    ReportProgress(runDetail);
                    iterationResults.Add(runDetail);

                    // if model is perfect, break
                    if (_metricsAgent.IsModelPerfect(suggestedPipelineRunDetail.Score))
                    {
                        break;
                    }

                    // If after third run, all runs have failed so far, throw exception
                    if (_history.Count() == 3 && _history.All(r => !r.RunSucceeded))
                    {
                        throw new InvalidOperationException($"Training failed with the exception: {_history.Last().Exception}");
                    }
                }
                catch (OperationCanceledException e)
                {
                    // This exception is thrown when the IHost/MLContext of the trainer is canceled due to
                    // reaching maximum experiment time. Simply catch this exception and return finished
                    // iteration results.
                    _logger.Warning(_operationCancelledMessage, e.Message);
                    return iterationResults;
                }
                catch (AggregateException e)
                {
                    // This exception is thrown when the IHost/MLContext of the trainer is canceled due to
                    // reaching maximum experiment time. Simply catch this exception and return finished
                    // iteration results. For some trainers, like FastTree, because training is done in parallel
                    // in can throw multiple OperationCancelledExceptions. This causes them to be returned as an
                    // AggregateException and misses the first catch block. This is to handle that case.
                    if (e.InnerExceptions.All(exception => exception is OperationCanceledException))
                    {
                        _logger.Warning(_operationCancelledMessage, e.Message);
                        return iterationResults;
                    }

                    throw;
                }
            } while (_history.Count < _experimentSettings.MaxModels &&
                    !_experimentSettings.CancellationToken.IsCancellationRequested &&
                    !_experimentTimerExpired);
            return iterationResults;
        }

        private static DirectoryInfo GetModelDirectory(DirectoryInfo rootDir)
        {
            if (rootDir == null)
            {
                return null;
            }

            var experimentDirFullPath = Path.Combine(rootDir.FullName, $"experiment_{Path.GetRandomFileName()}");
            var experimentDirInfo = new DirectoryInfo(experimentDirFullPath);
            if (!experimentDirInfo.Exists)
            {
                experimentDirInfo.Create();
            }
            return experimentDirInfo;
        }

        private void ReportProgress(TRunDetail iterationResult)
        {
            try
            {
                _progressCallback?.Report(iterationResult);
            }
            catch (Exception ex)
            {
                _logger.Error($"Progress report callback reported exception {ex}");
            }
        }

        private void WriteIterationLog(SuggestedPipeline pipeline, SuggestedPipelineRunDetail runResult, Stopwatch stopwatch)
        {
            _logger.Trace($"{_history.Count}\t{runResult.Score}\t{stopwatch.Elapsed}\t{pipeline.ToString()}");
        }
    }
}
