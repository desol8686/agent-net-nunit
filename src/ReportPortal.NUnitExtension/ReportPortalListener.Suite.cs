using ReportPortal.Client.Abstractions.Models;
using ReportPortal.Client.Abstractions.Requests;
using ReportPortal.NUnitExtension.EventArguments;
using ReportPortal.Shared.Converters;
using ReportPortal.Shared.Execution.Metadata;
using ReportPortal.Shared.Reporter;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using System.Xml.XPath;

namespace ReportPortal.NUnitExtension
{
    public partial class ReportPortalListener
    {
        public delegate void SuiteStartedHandler(object sender, TestItemStartedEventArgs e);
        public static event SuiteStartedHandler BeforeSuiteStarted;
        public static event SuiteStartedHandler AfterSuiteStarted;

        private void StartSuite(string report)
        {
            var xElement = XElement.Parse(report);

            try
            {
                var type = xElement.Attribute("type").Value;

                var id = xElement.Attribute("id").Value;
                var parentId = xElement.Attribute("parentId").Value;
                var name = xElement.Attribute("name").Value;
                var fullname = xElement.Attribute("fullname").Value;

                var startTime = DateTime.UtcNow;

                var startSuiteRequest = new StartTestItemRequest
                {
                    StartTime = startTime,
                    Name = name,
                    Type = TestItemType.Suite
                };

                var beforeSuiteEventArg = new TestItemStartedEventArgs(_rpService, startSuiteRequest, null, report);

                var rootNamespaces = Config.GetValues<string>("rootNamespaces", null);
                if (rootNamespaces != null && rootNamespaces.Any(n => n == name))
                {
                    beforeSuiteEventArg.Canceled = true;
                }

                if (!beforeSuiteEventArg.Canceled)
                {
                    try
                    {
                        BeforeSuiteStarted?.Invoke(this, beforeSuiteEventArg);
                    }
                    catch (Exception exp)
                    {
                        _traceLogger.Error("Exception was thrown in 'BeforeSuiteStarted' subscriber." + Environment.NewLine + exp);
                    }
                }

                // TODO: Отложенное создание Suite - не создаем TestReporter сразу, а только сохраняем информацию
                // Это позволяет не стартовать namespace'ы преждевременно в ReportPortal
                if (!beforeSuiteEventArg.Canceled)
                {
                    // Создаем FlowItemInfo без TestReporter - он будет создан позже при реальном запуске тестов
                    _flowItems[id] = new FlowItemInfo(id, parentId, FlowItemInfo.FlowType.Suite, name, null, startTime)
                    {
                        PendingStartSuiteRequest = startSuiteRequest,
                        PendingStartSuiteReport = report
                    };
                }
                else
                {
                    _flowItems[id] = new FlowItemInfo(id, parentId, FlowItemInfo.FlowType.Suite, name, null, startTime);
                }
            }
            catch (Exception exception)
            {
                _traceLogger.Error("ReportPortal exception was thrown." + Environment.NewLine + exception);
            }
        }

        /// <summary>
        /// Создать TestReporter для Suite если он еще не создан (отложенное создание).
        /// </summary>
        /// <param name="suiteId">ID Suite для создания</param>
        private void EnsureSuiteReporterCreated(string suiteId)
        {
            if (!_flowItems.ContainsKey(suiteId))
                return;

            var suiteFlowItem = _flowItems[suiteId];
            
            // Если TestReporter уже создан, ничего не делаем
            if (suiteFlowItem.TestReporter != null)
                return;

            // Если нет отложенного запроса, значит Suite был отменен
            if (suiteFlowItem.PendingStartSuiteRequest == null)
                return;

            try
            {
                ITestReporter suiteReporter;

                if (string.IsNullOrEmpty(suiteFlowItem.ParentId) || !_flowItems.ContainsKey(suiteFlowItem.ParentId))
                {
                    suiteReporter = _launchReporter.StartChildTestReporter(suiteFlowItem.PendingStartSuiteRequest);
                }
                else
                {
                    // Убеждаемся что родительский Suite тоже создан
                    EnsureSuiteReporterCreated(suiteFlowItem.ParentId);
                    
                    var parentFlowItem = FindReportedParentFlowItem(suiteFlowItem.ParentId);
                    if (parentFlowItem == null)
                    {
                        suiteReporter = _launchReporter.StartChildTestReporter(suiteFlowItem.PendingStartSuiteRequest);
                    }
                    else
                    {
                        suiteReporter = parentFlowItem.TestReporter.StartChildTestReporter(suiteFlowItem.PendingStartSuiteRequest);
                    }
                }

                // Обновляем время старта на текущее время (реальный момент запуска)
                var actualStartTime = DateTime.UtcNow;
                
                // Обновляем время старта в запросе для корректного отображения в ReportPortal
                suiteFlowItem.PendingStartSuiteRequest.StartTime = actualStartTime;
                
                // Обновляем FlowItemInfo с созданным TestReporter и актуальным временем старта
                _flowItems[suiteId] = new FlowItemInfo(suiteId, suiteFlowItem.ParentId, 
                    FlowItemInfo.FlowType.Suite, suiteFlowItem.FullName, suiteReporter, actualStartTime);

                try
                {
                    AfterSuiteStarted?.Invoke(this, new TestItemStartedEventArgs(_rpService, 
                        suiteFlowItem.PendingStartSuiteRequest, suiteReporter, suiteFlowItem.PendingStartSuiteReport));
                }
                catch (Exception exp)
                {
                    _traceLogger.Error("Exception was thrown in 'AfterSuiteStarted' subscriber." + Environment.NewLine + exp);
                }
            }
            catch (Exception exception)
            {
                _traceLogger.Error("ReportPortal exception was thrown during deferred suite creation." + Environment.NewLine + exception);
            }
        }

        private FlowItemInfo FindReportedParentFlowItem(string id)
        {
            if (_flowItems[id].TestReporter != null)
            {
                return _flowItems[id];
            }
            else if (!string.IsNullOrEmpty(_flowItems[id].ParentId))
            {
                return FindReportedParentFlowItem(_flowItems[id].ParentId);
            }
            else return null;
        }

        public delegate void SuiteFinishedHandler(object sender, TestItemFinishedEventArgs e);
        public static event SuiteFinishedHandler BeforeSuiteFinished;
        public static event SuiteFinishedHandler AfterSuiteFinished;

        private void FinishSuite(string report)
        {
            var xElement = XElement.Parse(report);

            try
            {
                var type = xElement.Attribute("type").Value;

                var id = xElement.Attribute("id").Value;
                var result = xElement.Attribute("result").Value;
                var parentId = xElement.Attribute("parentId");
                var duration = float.Parse(xElement.Attribute("duration").Value, System.Globalization.CultureInfo.InvariantCulture);

                // at the end of execution nunit raises 2 the same events, we need only that which has 'parentId' xml tag
                if (parentId != null)
                {
                    if (!_flowItems.ContainsKey(id))
                    {
                        StartSuite(report);
                    }

                    if (_flowItems.ContainsKey(id))
                    {
                        // Убеждаемся, что Suite действительно создан в ReportPortal (OneTimeSetup мог упасть до старта тестов)
                        if (_flowItems[id].TestReporter == null)
                        {
                            // Пытаемся отложенно создать Suite перед завершением
                            EnsureSuiteReporterCreated(id);

                            // Если по каким-то причинам создать не удалось (например, Suite был отменен),
                            // больше ничего сделать нельзя — удаляем из трекинга и завершаем обработку
                            if (_flowItems[id].TestReporter == null)
                            {
                                _flowItems.Remove(id);
                                return;
                            }
                        }

                        // finishing suite
                        // Используем текущее время как EndTime для корректного отображения duration
                        // Так как StartTime был обновлен на момент реального создания Suite
                        var finishSuiteRequest = new FinishTestItemRequest
                        {
                            EndTime = DateTime.UtcNow,
                            Status = _statusMap[result]
                        };

                        // adding categories to suite
                        var categories = xElement.XPathSelectElements("//properties/property[@name='Category']");
                        if (categories != null)
                        {
                            if (finishSuiteRequest.Attributes == null)
                            {
                                finishSuiteRequest.Attributes = new List<ItemAttribute>();
                            }

                            foreach (XElement category in categories)
                            {
                                var value = category.Attribute("value").Value;

                                if (!string.IsNullOrEmpty(value))
                                {
                                    var attr = new ItemAttributeConverter().ConvertFrom(value, opts => opts.UndefinedKey = "Category");

                                    finishSuiteRequest.Attributes.Add(attr);
                                }
                            }
                        }

                        // adding author attribute to suite
                        var authorElements = xElement.XPathSelectElements("//properties/property[@name='Author']");
                        if (authorElements != null)
                        {
                            if (finishSuiteRequest == null)
                            {
                                finishSuiteRequest.Attributes = new List<ItemAttribute>();
                            }

                            foreach (XElement authorElement in authorElements)
                            {
                                var value = authorElement.Attribute("value").Value;

                                if (!string.IsNullOrEmpty(value))
                                {
                                    var attr = new ItemAttribute { Key = "Author", Value = value };

                                    finishSuiteRequest.Attributes.Add(attr);
                                }
                            }
                        }

                        // adding description to suite
                        var description = xElement.XPathSelectElement("//properties/property[@name='Description']");
                        if (description != null)
                        {
                            finishSuiteRequest.Description = description.Attribute("value").Value;
                        }

                        var eventArg = new TestItemFinishedEventArgs(_rpService, finishSuiteRequest, _flowItems[id].TestReporter, report);

                        try
                        {
                            BeforeSuiteFinished?.Invoke(this, eventArg);
                        }
                        catch (Exception exp)
                        {
                            _traceLogger.Error("Exception was thrown in 'BeforeSuiteFinished' subscriber." + Environment.NewLine + exp);
                        }

                        Action<string, FinishTestItemRequest, string, string> finishSuiteAction = (__id, __finishSuiteRequest, __report, __parentstacktrace) =>
                        {
                            // Проверяем, что элемент еще существует (deferred action может быть вызван позже)
                            if (!_flowItems.ContainsKey(__id))
                            {
                                return;
                            }

                            // find all defferred children test items to finish
                            var deferredFlowItems = _flowItems.Where(fi => fi.Value.ParentId == __id && fi.Value.DeferredFinishAction != null).Select(fi => fi.Value).ToList();
                            foreach (var deferredFlowItem in deferredFlowItems)
                            {
                                deferredFlowItem.DeferredFinishAction.Invoke(deferredFlowItem.Id, deferredFlowItem.FinishTestItemRequest, deferredFlowItem.Report, __parentstacktrace);
                            }

                            var testReporter = _flowItems[__id].TestReporter;
                            
                            // Если есть стек из OneTimeSetup, пишем его в лог Suite
                            if (!string.IsNullOrEmpty(__parentstacktrace) && testReporter != null)
                            {
                                testReporter.Log(new CreateLogItemRequest
                                {
                                    Level = LogLevel.Error,
                                    Time = DateTime.UtcNow,
                                    Text = __parentstacktrace
                                });
                            }
                            
                            testReporter?.Finish(__finishSuiteRequest);

                            if (testReporter != null)
                            {
                                try
                                {
                                    AfterSuiteFinished?.Invoke(this, new TestItemFinishedEventArgs(_rpService, __finishSuiteRequest, testReporter, __report));
                                }
                                catch (Exception exp)
                                {
                                    _traceLogger.Error("Exception was thrown in 'AfterSuiteFinished' subscriber." + Environment.NewLine + exp);
                                }
                            }

                            _flowItems.Remove(__id);
                        };

                        // understand whether finishing test suite should be defferred. Usually we need it to report stacktrace in case of OneTimeSetup method fails, and stacktrace is avalable later in "FinishSuite" method
                        if (xElement.Attribute("site")?.Value == "Parent")
                        {
                            _flowItems[id].FinishTestItemRequest = finishSuiteRequest;
                            _flowItems[id].Report = report;
                            _flowItems[id].DeferredFinishAction = finishSuiteAction;
                        }
                        else
                        {
                            var failurestacktrace = xElement.XPathSelectElement("//failure/stack-trace")?.Value;

                            finishSuiteAction.Invoke(id, finishSuiteRequest, report, failurestacktrace);
                        }
                    }
                }

            }
            catch (Exception exception)
            {
                _traceLogger.Error("ReportPortal exception was thrown." + Environment.NewLine + exception);
            }
        }
    }
}