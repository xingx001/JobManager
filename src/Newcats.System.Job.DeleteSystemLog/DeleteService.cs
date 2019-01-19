﻿using System;
using System.Collections.Generic;
using Newcats.JobManager.Common.DataAccess;
using Newcats.JobManager.Common.Entity;

namespace Newcats.System.Job.DeleteSystemLog
{
    public class DeleteService
    {
        private readonly Repository<JobLogEntity, long> _repository;

        public DeleteService()
        {
            _repository = new Repository<JobLogEntity, long>();
        }

        public bool DeleteSystemLog()
        {
            return _repository.Delete(new List<DbWhere<JobLogEntity>>
            {
                new DbWhere<JobLogEntity>(j => j.CreateTime, DateTime.Now.AddMonths(-1), OperateType.LessEqual)
            }, commandTimeout: 600) > 0;
        }
    }
}