﻿using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;

namespace Sungero.Docflow.Server
{
  public class ModuleAsyncHandlers
  {

    public virtual void AddRegistrationStamp(Sungero.Docflow.Server.AsyncHandlerInvokeArgs.AddRegistrationStampInvokeArgs args)
    {
      int documentId = args.DocumentId;
      int versionId = args.VersionId;
      double rightIndent = args.RightIndent;
      double bottomIndent = args.BottomIndent;
      
      Logger.DebugFormat("AddRegistrationStamp: start convert document to pdf. Document id - {0}.", documentId);
      
      var document = OfficialDocuments.GetAll(x => x.Id == documentId).FirstOrDefault();
      if (document == null)
      {
        Logger.DebugFormat("AddRegistrationStamp: not found document with id {0}.", documentId);
        return;
      }
      
      var version = document.Versions.SingleOrDefault(v => v.Id == versionId);
      if (version == null)
      {
        Logger.DebugFormat("AddRegistrationStamp: not found version. Document id - {0}, version number - {1}.", documentId, versionId);
        return;
      }
      
      if (!Locks.TryLock(version.Body))
      {
        Logger.DebugFormat("AddRegistrationStamp: version is locked. Document id - {0}, version number - {1}.", documentId, versionId);
        args.Retry = true;
        return;
      }
      
      var registrationStamp = Docflow.Functions.OfficialDocument.GetRegistrationStampAsHtml(document);
      var result = Docflow.Functions.Module.ConvertToPdfWithStamp(document, versionId, registrationStamp, false, rightIndent, bottomIndent);
      Locks.Unlock(version.Body);
      
      if (result.HasErrors)
      {
        Logger.DebugFormat("AddRegistrationStamp: {0}", result.ErrorMessage);
        if (result.HasLockError)
        {
          args.Retry = true;
        }
        else
        {
          var operation = new Enumeration(Constants.OfficialDocument.Operation.ConvertToPdf);
          document.History.Write(operation, operation, string.Empty, version.Number);
          document.Save();
        }
      }
      
      Logger.DebugFormat("AddRegistrationStamp: convert document {0} to pdf successfully.", documentId);
    }

    public virtual void ExecuteApprovalFunction(Sungero.Docflow.Server.AsyncHandlerInvokeArgs.ExecuteApprovalFunctionInvokeArgs args)
    {
      Logger.DebugFormat("ExecuteApprovalFunction: Start for queue item id {0}. Retry iteration: {1}", args.QueueItemId, args.RetryIteration);
      
      var queueItem = ApprovalFunctionQueueItems.GetAll(t => t.Id == args.QueueItemId).FirstOrDefault();
      if (queueItem == null)
      {
        Logger.DebugFormat("ExecuteApprovalFunction: asynchronous handler was terminated. Queue item {0} not found", args.QueueItemId);
        return;
      }

      var task = ApprovalTasks.GetAll(t => t.Id == queueItem.TaskId).FirstOrDefault();
      if (task == null)
      {
        Logger.DebugFormat("ExecuteApprovalFunction: asynchronous handler was terminated. Task {0} not found", queueItem.TaskId);
        ApprovalFunctionQueueItems.Delete(queueItem);
        return;
      }

      if (task.Status != Docflow.ApprovalTask.Status.InProcess)
      {
        Logger.DebugFormat("ExecuteApprovalFunction: asynchronous handler was terminated. Task status {0}", task.Status);
        ApprovalFunctionQueueItems.Delete(queueItem);
        return;
      }
      
      if (task.StartId != queueItem.TaskStartId)
      {
        Logger.DebugFormat("ExecuteApprovalFunction: asynchronous handler was terminated. StartId was changed. Value in queue item {0}, value in task {1} ", queueItem.TaskStartId, task.StartId);
        ApprovalFunctionQueueItems.Delete(queueItem);
        return;
      }
      
      var stage = task.ApprovalRule.Stages
        .Where(s => s.StageType == Docflow.ApprovalRuleBaseStages.StageType.Function)
        .FirstOrDefault(s => s.Number == task.StageNumber);
      if (stage == null)
      {
        Logger.DebugFormat("ExecuteApprovalFunction: asynchronous handler was terminated. Stage with type 'Function' not found, current stage number {0}", task.StageNumber);
        ApprovalFunctionQueueItems.Delete(queueItem);
        return;
      }
      
      var approvalFunctionStage = ApprovalFunctionStageBases.As(stage.StageBase);
      if (approvalFunctionStage == null)
      {
        Logger.Debug("ExecuteApprovalFunction: asynchronous handler was terminated. Property 'StageBase' is not 'ApprovalFunctionStageBase' type");
        ApprovalFunctionQueueItems.Delete(queueItem);
        return;
      }
      
      var result = Docflow.Functions.ApprovalFunctionStageBase.GetSuccessResult(approvalFunctionStage);
      
      try
      {
        result = Functions.ApprovalFunctionStageBase.Execute(approvalFunctionStage, task);
      }
      catch (Exception ex)
      {
        result = Docflow.Functions.ApprovalFunctionStageBase.GetRetryResult(approvalFunctionStage, ex.Message);
        Logger.ErrorFormat("ExecuteApprovalFunction: unhandled exception in method 'Execute'", ex);
      }
      
      queueItem = ApprovalFunctionQueueItems.GetAll(t => t.Id == args.QueueItemId).FirstOrDefault();
      if (queueItem == null)
      {
        Logger.DebugFormat("ExecuteApprovalFunction: asynchronous handler was terminated. Queue item {0} not found after execute", args.QueueItemId);
        return;
      }
      
      if (result.Success)
      {
        queueItem.ProcessingStatus = Docflow.ApprovalFunctionQueueItem.ProcessingStatus.Completed;
        queueItem.Save();
        task.Blocks.ExecuteAllMonitoringBlocks();
        Logger.Debug("ExecuteApprovalFunction: function execution result - success, processing status: completed");
      }
      else
      {
        if (!result.Retry)
        {
          queueItem.ProcessingStatus = Docflow.ApprovalFunctionQueueItem.ProcessingStatus.Error;
          queueItem.ErrorMessage = result.ErrorMessage;
          queueItem.Save();
          task.Blocks.ExecuteAllMonitoringBlocks();
          Logger.DebugFormat("ExecuteApprovalFunction: function execution result - error without retry, processing status: error, message: {0}", result.ErrorMessage);
        }
        else
        {
          args.Retry = true;
          queueItem.ErrorMessage = result.ErrorMessage;
          queueItem.Save();
          Logger.DebugFormat("ExecuteApprovalFunction: function execution result - error, asynс handler will be retried, message: {0}", result.ErrorMessage);
        }
      }
      
      Logger.DebugFormat("ExecuteApprovalFunction: Done for queue item id {0}", args.QueueItemId);
    }

    /// <summary>
    /// Установка статусов документа при прекращении задачи на согласование.
    /// </summary>
    /// <param name="args">Параметры вызова асинхронного обработчика.</param>
    public virtual void SetDocumentStateAborted(Sungero.Docflow.Server.AsyncHandlerInvokeArgs.SetDocumentStateAbortedInvokeArgs args)
    {
      var task = ApprovalTasks.GetAll(t => t.Id == args.TaskId).FirstOrDefault();
      if (task == null)
        return;
      
      var document = task.DocumentGroup.OfficialDocuments.FirstOrDefault();
      if (document == null)
        return;
      
      var documentTaskIds = Functions.OfficialDocument.GetTaskIdsWhereDocumentInRequredGroup(document);
      var hasNewApprovalTasks = ApprovalTasks.GetAll(t => documentTaskIds.Contains(t.Id) && t.Started > args.AbortedDate).Any();
      if (hasNewApprovalTasks)
      {
        Logger.DebugFormat("SetDocumentStateAborted: has new tasks for document {0}", document.Id);
        return;
      }
      
      if (args.NeedSetState)
      {
        Functions.ApprovalTask.SetDocumentStateAborted(task, args.SetObsolete);
        document.Save();
      }
      
      if (args.NeedGrantAccessRightsOnDocument)
        Functions.ApprovalTask.GrantAccessRightsForAttachmentsToInitiatorOnAbort(task);
    }

    /// <summary>
    /// Копирование номенклатуры дел.
    /// </summary>
    /// <param name="args">Параметры вызова асинхронного обработчика.</param>
    public virtual void CopyCaseFiles(Sungero.Docflow.Server.AsyncHandlerInvokeArgs.CopyCaseFilesInvokeArgs args)
    {
      PublicFunctions.CaseFile.Remote.CopyCaseFiles(args.UserId,
                                                    args.SourcePeriodStartDate, args.SourcePeriodEndDate,
                                                    args.TargetPeriodStartDate, args.TargetPeriodEndDate,
                                                    args.BusinessUnitId,
                                                    args.DepartmentId);
    }
    
    /// <summary>
    /// Сконвертировать документы в pdf.
    /// </summary>
    /// <param name="args">Параметры вызова асинхронного обработчика.</param>
    public virtual void ConvertDocumentToPdf(Sungero.Docflow.Server.AsyncHandlerInvokeArgs.ConvertDocumentToPdfInvokeArgs args)
    {
      int documentId = args.DocumentId;
      int versionId = args.VersionId;
      
      Logger.DebugFormat("ConvertDocumentToPdf: start convert document to pdf. Document id - {0}.", documentId);
      
      var document = OfficialDocuments.GetAll(x => x.Id == documentId).FirstOrDefault();
      if (document == null)
      {
        Logger.DebugFormat("ConvertDocumentToPdf: not found document with id {0}.", documentId);
        return;
      }
      
      var version = document.Versions.SingleOrDefault(v => v.Id == versionId);
      if (version == null)
      {
        Logger.DebugFormat("ConvertDocumentToPdf: not found version. Document id - {0}, version number - {1}.", documentId, versionId);
        return;
      }
      
      if (!Locks.TryLock(version.Body))
      {
        Logger.DebugFormat("ConvertDocumentToPdf: version is locked. Document id - {0}, version number - {1}.", documentId, versionId);
        args.Retry = true;
        return;
      }
      
      var result = Docflow.Functions.OfficialDocument.ConvertToPdfAndAddSignatureMark(document, version.Id);
      Locks.Unlock(version.Body);
      
      if (result.HasErrors)
      {
        Logger.DebugFormat("ConvertDocumentToPdf: {0}", result.ErrorMessage);
        if (result.HasLockError)
        {
          args.Retry = true;
        }
        else
        {
          var operation = new Enumeration(Constants.OfficialDocument.Operation.ConvertToPdf);
          document.History.Write(operation, operation, string.Empty, version.Number);
          document.Save();
        }
      }
      
      Logger.DebugFormat("ConvertDocumentToPdf: convert document {0} to pdf successfully.", documentId);
    }

    public virtual void SetDocumentStorage(Sungero.Docflow.Server.AsyncHandlerInvokeArgs.SetDocumentStorageInvokeArgs args)
    {
      int documentId = args.DocumentId;
      int storageId = args.StorageId;
      
      Logger.DebugFormat("SetDocumentStorage: start set storage to document {0}.", documentId);
      
      var document = OfficialDocuments.GetAll(d => d.Id == documentId).FirstOrDefault();
      if (document == null)
      {
        Logger.DebugFormat("SetDocumentStorage: not found document with id {0}.", documentId);
        return;
      }
      
      var storage = Storages.GetAll(s => s.Id == storageId).FirstOrDefault();
      if (storage == null)
      {
        Logger.DebugFormat("SetDocumentStorage: not found storage with id {0}.", storageId);
        return;
      }
      
      if (Locks.GetLockInfo(document).IsLockedByOther)
      {
        Logger.DebugFormat("SetDocumentStorage: cannot change storage, document {0} is locked.", documentId);
        args.Retry = true;
        return;
      }
      
      var versions = document.Versions.Where(v => !Equals(v.Body.Storage, storage) || !Equals(v.PublicBody.Storage, storage));
      var retry = false;
      foreach (var version in versions)
      {
        if (Locks.GetLockInfo(version.Body).IsLockedByOther)
        {
          Logger.DebugFormat("SetDocumentStorage: cannot change storage, body is locked. Document {0} (version id {1}).", documentId, version.Id);
          retry = true;
        }
        if (Locks.GetLockInfo(version.PublicBody).IsLockedByOther)
        {
          Logger.DebugFormat("SetDocumentStorage: cannot change storage, public body is locked. Document {0} (version id {1}).", documentId, version.Id);
          retry = true;
        }
      }
      if (retry)
      {
        args.Retry = true;
        return;
      }
      
      try
      {
        foreach (var version in versions)
        {
          if (!Equals(version.Body.Storage, storage))
            version.Body.SetStorage(storage);
          
          if (!Equals(version.PublicBody.Storage, storage))
            version.PublicBody.SetStorage(storage);
        }
        
        document.Storage = storage;
        
        ((Domain.Shared.IExtendedEntity)document).Params[Docflow.PublicConstants.OfficialDocument.DontUpdateModified] = true;
        document.Save();
      }
      catch (Exception ex)
      {
        Logger.Error("SetDocumentStorage: cannot change storage.", ex);
        args.Retry = true;
        return;
      }
      Logger.DebugFormat("SetDocumentStorage: set storage to document {0} successfully.", documentId);
      
    }

    /// <summary>
    /// Асинхронная выдача прав на документы от правила.
    /// </summary>
    /// <param name="args">Параметры вызова асинхронного обработчика.</param>
    public virtual void GrantAccessRightsToDocumentsByRule(Sungero.Docflow.Server.AsyncHandlerInvokeArgs.GrantAccessRightsToDocumentsByRuleInvokeArgs args)
    {
      int ruleId = args.RuleId;
      
      Logger.DebugFormat("GrantAccessRightsToDocumentsByRule: start create documents queue for rule {0}", ruleId);
      
      var rule = AccessRightsRules.GetAll(r => r.Id == ruleId).FirstOrDefault();
      if (rule == null)
        return;
      
      foreach (var ruleDocument in GetDocumentsByRule(rule))
      {
        PublicFunctions.Module.CreateGrantAccessRightsToDocumentAsyncHandler(ruleDocument, rule.Id, true);
        Logger.DebugFormat("GrantAccessRightsToDocumentsByRule: create document queue for document {0}, rule {1}", ruleDocument, ruleId);
      }
      
      Logger.DebugFormat("GrantAccessRightsToDocumentsByRule: success create documents queue for rule {0}", ruleId);
    }
    
    /// <summary>
    /// Асинхронная выдача прав на документ.
    /// </summary>
    /// <param name="args">Параметры вызова асинхронного обработчика.</param>
    public virtual void GrantAccessRightsToDocument(Sungero.Docflow.Server.AsyncHandlerInvokeArgs.GrantAccessRightsToDocumentInvokeArgs args)
    {

      int documentId = args.DocumentId;
      int ruleId = args.RuleId;
      
      Logger.DebugFormat("GrantAccessRightsToDocument: start grant rights for document {0}, rule {1}", documentId, ruleId);
      
      var isGranted = Docflow.Functions.Module.GrantAccessRightsToDocumentByRule(documentId, ruleId, args.GrantRightToChildDocuments);
      if (!isGranted)
      {
        Logger.DebugFormat("GrantAccessRightsToDocument: cannot grant rights for document {0}, rule {1}", documentId, ruleId);
        args.Retry = true;
      }
      else
        Logger.DebugFormat("GrantAccessRightsToDocument: success grant rights for document {0}, rule {1}", documentId, ruleId);
    }
    
    /// <summary>
    /// Фильтр для категорий договоров.
    /// </summary>
    /// <param name="rule">Правило.</param>
    /// <param name="query">Ленивый запрос документов.</param>
    /// <returns>Относительно ленивый запрос с категориями.</returns>
    private static IEnumerable<int> FilterDocumentsByGroups(IAccessRightsRule rule, IQueryable<IOfficialDocument> query)
    {
      foreach (var document in query)
      {
        var documentGroup = Functions.OfficialDocument.GetDocumentGroup(document);
        if (rule.DocumentGroups.Any(k => Equals(k.DocumentGroup, documentGroup)))
          yield return document.Id;
      }
    }
    
    /// <summary>
    /// Получить документы по правилу.
    /// </summary>
    /// <param name="rule">Правило.</param>
    /// <returns>Документы по правилу.</returns>
    public static IEnumerable<int> GetDocumentsByRule(IAccessRightsRule rule)
    {
      var documentKinds = rule.DocumentKinds.Select(t => t.DocumentKind).ToList();
      var businessUnits = rule.BusinessUnits.Select(t => t.BusinessUnit).ToList();
      var departments = rule.Departments.Select(t => t.Department).ToList();
      
      var documents = OfficialDocuments.GetAll()
        .Where(d => !documentKinds.Any() || documentKinds.Contains(d.DocumentKind))
        .Where(d => !businessUnits.Any() || businessUnits.Contains(d.BusinessUnit))
        .Where(d => !departments.Any() || departments.Contains(d.Department));
      
      if (rule.DocumentGroups.Any())
        return FilterDocumentsByGroups(rule, documents);
      else
        return documents.Select(d => d.Id);
    }

  }
}