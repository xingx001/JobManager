﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newcats.JobManager.Common.Entity;
using Newcats.JobManager.Host.Service;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Triggers;

namespace Newcats.JobManager.Host.Manager
{
    /// <summary>
    /// QuartzJob的管理类
    /// </summary>
    public class QuartzManager
    {
        /// <summary>
        /// Job调度
        /// </summary>
        /// <param name="scheduler"></param>
        public static async Task ManagerScheduler(IScheduler scheduler)
        {
            KeepSystemJobRunning(scheduler);
            IEnumerable<JobInfoEntity> list = new JobService().GetAllowScheduleJobs();
            if (list != null && list.Any())
            {
                foreach (JobInfoEntity jobInfo in list)
                {
                    JobKey jobKey = new JobKey(jobInfo.Id.ToString(), jobInfo.Id.ToString() + "Group");
                    if (await scheduler.CheckExists(jobKey) == false)//不存在调度器中，添加
                    {
                        if (jobInfo.State == JobState.Starting)
                        {
                            ManagerJob(scheduler, jobInfo);//添加job到调度器
                            if (await scheduler.CheckExists(jobKey) == false)//添加失败
                                new JobService().UpdateJobState(jobInfo.Id, JobState.Stopped);
                            else
                                new JobService().UpdateJobState(jobInfo.Id, JobState.Running);
                        }
                        else if (jobInfo.State == JobState.Stopping)
                        {
                            new JobService().UpdateJobState(jobInfo.Id, JobState.Stopped);
                        }
                        else if (jobInfo.State == JobState.Updating)
                        {
                            ManagerJob(scheduler, jobInfo);//添加job到调度器
                            if (await scheduler.CheckExists(jobKey) == false)//添加失败
                                new JobService().UpdateJobState(jobInfo.Id, JobState.Stopped);
                            else
                                new JobService().UpdateJobState(jobInfo.Id, JobState.Running);
                        }
                    }
                    else//job已存在调度器中，停止或启动调度
                    {
                        if (jobInfo.State == JobState.Stopping)
                        {
                            await scheduler.DeleteJob(jobKey);
                            new JobService().UpdateJobState(jobInfo.Id, JobState.Stopped);
                        }
                        else if (jobInfo.State == JobState.Starting)
                        {
                            new JobService().UpdateJobState(jobInfo.Id, JobState.Running);
                        }
                        else if (jobInfo.State == JobState.Updating)
                        {
                            await scheduler.DeleteJob(jobKey);
                            ManagerJob(scheduler, jobInfo);//添加job到调度器
                            if (await scheduler.CheckExists(jobKey) == false)//添加失败
                                new JobService().UpdateJobState(jobInfo.Id, JobState.Stopped);
                            else
                                new JobService().UpdateJobState(jobInfo.Id, JobState.Running);
                        }
                        else if (jobInfo.State == JobState.FireNow)
                        {
                            await scheduler.TriggerJob(jobKey);
                            new JobService().UpdateJobState(jobInfo.Id, JobState.Running);
                        }
                    }
                }
            }
            CheckRunningJobs(scheduler);//数据量大了之后，可能会加重数据库的负担，先尝试关闭的时候把running的状态改为starting
        }

        /// <summary>
        /// 从程序集中加载指定类
        /// </summary>
        /// <param name="assemblyName">含后缀的程序集名</param>
        /// <param name="className">含命名空间完整类名</param>
        /// <returns></returns>
        private static Type GetClassInfo(string assemblyName, string className)
        {
            Type type = null;
            try
            {
                string file = GetAbsolutePath(assemblyName);
                Assembly assembly = Assembly.LoadFrom(file);
                type = assembly.GetType(className, true, true);
            }
            catch (Exception e)
            {
                //_log.Error($"动态加载类型失败。程序集:{assemblyName}|类名:{className}。", e);
            }
            return type;
        }

        /// <summary>
        ///  获取文件的绝对路径
        /// </summary>
        /// <param name="relativePath">相对路径</param>
        /// <returns></returns>
        private static string GetAbsolutePath(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                throw new ArgumentNullException(nameof(relativePath));
            }
            relativePath = relativePath.Replace("/", "\\");
            if (relativePath[0] == '\\')
            {
                relativePath = relativePath.Remove(0, 1);
            }
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativePath);
        }

        /// <summary>
        /// 把新加的Job添加到调度器
        /// </summary>
        /// <param name="scheduler">调度器</param>
        /// <param name="jobInfo">Job信息</param>
        private static async void ManagerJob(IScheduler scheduler, JobInfoEntity jobInfo)
        {
            if (CronExpression.IsValidExpression(jobInfo.CronExpression))
            {
                Type type = GetClassInfo(jobInfo.AssemblyName, jobInfo.ClassName);
                if (type != null)
                {
                    try
                    {
                        IJobDetail job = new JobDetailImpl(jobInfo.Id.ToString(), jobInfo.Id.ToString() + "Group", type);
                        job.JobDataMap.Add("Parameters", jobInfo.JobArgs);
                        job.JobDataMap.Add("JobName", jobInfo.Name);

                        CronTriggerImpl trigger = new CronTriggerImpl
                        {
                            CronExpressionString = jobInfo.CronExpression,
                            Name = jobInfo.Id.ToString(),
                            Group = $"{jobInfo.Id}TriggerGroup",
                            Description = jobInfo.Description,
                            TimeZone = TimeZoneInfo.Local
                        };

                        await scheduler.ScheduleJob(job, trigger);
                    }
                    catch (Exception e)
                    {
                        new JobService().InsertLog(new JobLogEntity
                        {
                            JobId = jobInfo.Id,
                            FireTime = DateTime.Now,
                            FireDuration = 0,
                            FireState = FireState.Error,
                            Content = $"{jobInfo.Name}启用失败,异常信息：{e.Message}！",
                            CreateTime = DateTime.Now
                        });
                        //_log.Error($"JobId:{jobInfo.Id}|JobName:{jobInfo.Name}启用失败！", e);
                    }
                }
                else
                {
                    new JobService().InsertLog(new JobLogEntity
                    {
                        JobId = jobInfo.Id,
                        FireTime = DateTime.Now,
                        FireDuration = 0,
                        FireState = FireState.Error,
                        Content = $"{jobInfo.Name}启用失败,请检查此Job执行类库是否上传到正确位置！",
                        CreateTime = DateTime.Now
                    });
                }
            }
            else
            {
                new JobService().InsertLog(new JobLogEntity
                {
                    JobId = jobInfo.Id,
                    FireTime = DateTime.Now,
                    FireDuration = 0,
                    FireState = FireState.Error,
                    Content = $"{jobInfo.Name}启用失败,Cron表达式错误！",
                    CreateTime = DateTime.Now
                });
            }
        }

        /// <summary>
        /// 保证系统作业正常运行
        /// </summary>
        private static async void KeepSystemJobRunning(IScheduler scheduler)
        {
            JobInfoEntity job = new JobService().GetSystemMainJobAsync();
            if (job == null)
            {
                job = new JobInfoEntity();
                job.JobLevel = JobLevel.System;
                job.Name = "系统作业";
                job.Description = "负责调度其他作业的系统作业，不能删除/停止/禁用";
                job.AssemblyName = Assembly.GetExecutingAssembly().ManifestModule.Name;
                job.ClassName = typeof(SystemJob).FullName;
                job.CronExpression = "0/15 * * * * ?";
                job.CronExpressionDescription = "每隔15秒执行一次";
                job.State = JobState.Starting;
                job.Disabled = false;
                job.CreateName = "AtFirstRun";
                job.CreateTime = DateTime.Now;
                job.Id = new JobService().InsertJob(job);
            }
            JobKey jobKey = new JobKey(job.Id.ToString(), job.Id.ToString() + "Group");
            if (await scheduler.CheckExists(jobKey) == false)
            {
                new JobService().SetSystemJobAvailable(job.Id);
            }
        }

        /// <summary>
        /// 每半小时检查一次正在允许的Job是否存在调度器中
        /// 操作系统重启之后，正在运行的job状态不会改变，导致其不会加入到调度管理器
        /// </summary>
        /// <param name="scheduler"></param>
        private static async void CheckRunningJobs(IScheduler scheduler)
        {
            if (DateTime.Now.Minute != 0 && DateTime.Now.Minute != 30)
                return;

            IEnumerable<JobInfoEntity> list = new JobService().GetAllRunningJobs();
            if (list == null || !list.Any())
                return;
            foreach (JobInfoEntity jobInfo in list)
            {
                JobKey jobKey = new JobKey(jobInfo.Id.ToString(), jobInfo.Id.ToString() + "Group");
                if (await scheduler.CheckExists(jobKey))//已存在调度器中
                    continue;

                ManagerJob(scheduler, jobInfo);//添加job到调度器
            }
        }

        /// <summary>
        /// windows服务停止或意外关闭时，需要把正在运行的Job状态改为Starting，以便重启后重新加入调度器
        /// </summary>
        public static void SchedulerStopping()
        {
            new JobService().SetRunningJobStarting();
        }
    }
}