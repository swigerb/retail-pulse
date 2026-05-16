using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Agents.Routing;
using RetailPulse.Api.Memory;

namespace RetailPulse.Tests.Parsing;

/// <summary>
/// Sprint 4 cleanup — verifies that JSON parse failures in
/// <see cref="RetailOpsRouter.ParseClassification"/> and
/// <see cref="MemoryExtractionService.ParseExtraction"/> are logged
/// at Debug level (not silently swallowed), and that valid input
/// does not produce error logs.
/// </summary>
public class ExceptionLoggingTests
{
    #region RetailOpsRouter.ParseClassification

    [Fact]
    public async Task ParseClassification_InvalidJson_LogsAtDebugLevel()
    {
        var logger = new Mock<ILogger<RetailOpsRouter>>();

        RetailOpsRouter.IntentClassification result = RetailOpsRouter.ParseClassification("NOT VALID JSON", logger.Object);

        result.Should().NotBeNull("parser should return a fallback, not throw");

        logger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => true),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce,
            "invalid JSON should produce a Debug-level log entry");

        await Task.CompletedTask;
    }

    [Fact]
    public async Task ParseClassification_ValidJson_DoesNotProduceErrorLogs()
    {
        var logger = new Mock<ILogger<RetailOpsRouter>>();
        string validJson = /*lang=json,strict*/ """{"intent":"general","confidence":0.9,"intents":["general"]}""";

        RetailOpsRouter.ParseClassification(validJson, logger.Object);

        logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => true),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never,
            "valid JSON should not produce any error-level log entries");

        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => true),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never,
            "valid JSON should not produce any warning-level log entries");

        await Task.CompletedTask;
    }

    [Fact]
    public async Task ParseClassification_InvalidJson_LogMessageContainsTypeName()
    {
        var logger = new Mock<ILogger<RetailOpsRouter>>();

        RetailOpsRouter.ParseClassification("{{{bad", logger.Object);

        logger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("IntentClassification")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce,
            "the structured log message should include the type name 'IntentClassification'");

        await Task.CompletedTask;
    }

    #endregion

    #region MemoryExtractionService.ParseExtraction

    [Fact]
    public async Task ParseExtraction_InvalidJson_LogsAtDebugLevel()
    {
        var logger = new Mock<ILogger<MemoryExtractionService>>();

        MemoryExtractionService.ExtractionResult result = MemoryExtractionService.ParseExtraction("NOT VALID JSON", logger.Object);

        result.Should().NotBeNull("parser should return a fallback, not throw");

        logger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => true),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce,
            "invalid JSON should produce a Debug-level log entry");

        await Task.CompletedTask;
    }

    [Fact]
    public async Task ParseExtraction_ValidJson_DoesNotProduceErrorLogs()
    {
        var logger = new Mock<ILogger<MemoryExtractionService>>();
        string validJson = /*lang=json,strict*/ """{"summary":"test","entities":["A"],"preference":null}""";

        MemoryExtractionService.ParseExtraction(validJson, logger.Object);

        logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => true),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never,
            "valid JSON should not produce any error-level log entries");

        await Task.CompletedTask;
    }

    [Fact]
    public async Task ParseExtraction_InvalidJson_LogMessageContainsTypeName()
    {
        var logger = new Mock<ILogger<MemoryExtractionService>>();

        MemoryExtractionService.ParseExtraction("<<<invalid>>>", logger.Object);

        logger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("ExtractionResult")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce,
            "the structured log message should include the type name 'ExtractionResult'");

        await Task.CompletedTask;
    }

    #endregion
}
