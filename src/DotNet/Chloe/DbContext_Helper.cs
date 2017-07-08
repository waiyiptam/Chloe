﻿using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Linq;
using Chloe.Query;
using Chloe.Core;
using Chloe.Infrastructure;
using Chloe.Descriptors;
using Chloe.DbExpressions;
using Chloe.Query.Internals;
using Chloe.Core.Visitors;
using Chloe.Exceptions;
using System.Data;
using Chloe.InternalExtensions;
using Chloe.Extensions;

namespace Chloe
{
    public abstract partial class DbContext : IDbContext, IDisposable
    {
        static Expression<Func<TEntity, bool>> BuildPredicate<TEntity>(object key)
        {
            Utils.CheckNull(key);

            Type entityType = typeof(TEntity);
            TypeDescriptor typeDescriptor = TypeDescriptor.GetDescriptor(entityType);
            EnsureEntityHasPrimaryKey(typeDescriptor);

            KeyValuePairList<MemberInfo, object> keyValueMap = new KeyValuePairList<MemberInfo, object>();

            if (typeDescriptor.PrimaryKeys.Count == 1)
            {
                keyValueMap.Add(typeDescriptor.PrimaryKeys[0].MemberInfo, key);
            }
            else
            {
                /*
                 * key: new { Key1 = "1", Key2 = "2" }
                 */

                object multipleKeyObject = key;
                Type multipleKeyObjectType = multipleKeyObject.GetType();

                for (int i = 0; i < typeDescriptor.PrimaryKeys.Count; i++)
                {
                    MappingMemberDescriptor keyMemberDescriptor = typeDescriptor.PrimaryKeys[i];
                    MemberInfo keyMember = multipleKeyObjectType.GetProperty(keyMemberDescriptor.MemberInfo.Name);
                    if (keyMember == null)
                        throw new ArgumentException(string.Format("The input object does not define member for key '{0}'.", keyMemberDescriptor.MemberInfo.Name));

                    object value = keyMember.GetMemberValue(multipleKeyObject);
                    if (value == null)
                        throw new ArgumentException(string.Format("The primary key '{0}' could not be null.", keyMemberDescriptor.MemberInfo.Name));

                    keyValueMap.Add(keyMemberDescriptor.MemberInfo, value);
                }
            }

            ParameterExpression parameter = Expression.Parameter(entityType, "a");
            Expression lambdaBody = null;

            foreach (var keyValue in keyValueMap)
            {
                Expression propOrField = Expression.PropertyOrField(parameter, keyValue.Key.Name);
                Expression wrappedValue = Chloe.Extensions.ExpressionExtension.MakeWrapperAccess(keyValue.Value, keyValue.Key.GetMemberType());
                Expression e = Expression.Equal(propOrField, wrappedValue);
                lambdaBody = lambdaBody == null ? e : Expression.AndAlso(lambdaBody, e);
            }

            Expression<Func<TEntity, bool>> predicate = Expression.Lambda<Func<TEntity, bool>>(lambdaBody, parameter);

            return predicate;
        }
        static void EnsureEntityHasPrimaryKey(TypeDescriptor typeDescriptor)
        {
            if (!typeDescriptor.HasPrimaryKey())
                throw new ChloeException(string.Format("The entity type '{0}' does not define a primary key.", typeDescriptor.EntityType.FullName));
        }
        static object ConvertIdentityType(object identity, Type conversionType)
        {
            if (identity.GetType() != conversionType)
                return Convert.ChangeType(identity, conversionType);

            return identity;
        }
        static List<Tuple<JoinType, Expression>> ResolveJoinInfo(LambdaExpression joinInfoExp)
        {
            /*
             * Useage:
             * var view = context.JoinQuery<User, City, Province, User, City>((user, city, province, user1, city1) => new object[] 
             * { 
             *     JoinType.LeftJoin, user.CityId == city.Id, 
             *     JoinType.RightJoin, city.ProvinceId == province.Id,
             *     JoinType.InnerJoin,user.Id==user1.Id,
             *     JoinType.FullJoin,city.Id==city1.Id
             * }).Select((user, city, province, user1, city1) => new { User = user, City = city, Province = province, User1 = user1, City1 = city1 });
             * 
             * To resolve join infomation:
             * JoinType.LeftJoin, user.CityId == city.Id               index of joinType is 0
             * JoinType.RightJoin, city.ProvinceId == province.Id      index of joinType is 2
             * JoinType.InnerJoin,user.Id==user1.Id                    index of joinType is 4
             * JoinType.FullJoin,city.Id==city1.Id                     index of joinType is 6
            */

            NewArrayExpression body = joinInfoExp.Body as NewArrayExpression;

            if (body == null)
            {
                throw new ArgumentException(string.Format("Invalid join infomation '{0}'. The correct usage is like: {1}", joinInfoExp, "context.JoinQuery<User, City>((user, city) => new object[] { JoinType.LeftJoin, user.CityId == city.Id })"));
            }

            List<Tuple<JoinType, Expression>> ret = new List<Tuple<JoinType, Expression>>();

            if ((joinInfoExp.Parameters.Count - 1) * 2 != body.Expressions.Count)
            {
                throw new ArgumentException(string.Format("Invalid join infomation '{0}'.", joinInfoExp));
            }

            for (int i = 0; i < joinInfoExp.Parameters.Count - 1; i++)
            {
                /*
                 * 0  0
                 * 1  2
                 * 2  4
                 * 3  6
                 * ...
                 */
                int indexOfJoinType = i * 2;

                Expression joinTypeExpression = body.Expressions[indexOfJoinType];
                object inputJoinType = ExpressionEvaluator.Evaluate(joinTypeExpression);
                if (inputJoinType == null || inputJoinType.GetType() != typeof(JoinType))
                    throw new ArgumentException(string.Format("Not support '{0}', please input correct type of 'Chloe.JoinType'.", joinTypeExpression));

                /*
                 * The next expression of join type must be join condition.
                 */
                Expression joinCondition = body.Expressions[indexOfJoinType + 1].StripConvert();

                if (joinCondition.Type != UtilConstants.TypeOfBoolean)
                {
                    throw new ArgumentException(string.Format("Not support '{0}', please input correct join condition.", joinCondition));
                }

                ParameterExpression[] parameters = joinInfoExp.Parameters.Take(i + 2).ToArray();

                List<Type> typeArguments = parameters.Select(a => a.Type).ToList();
                typeArguments.Add(UtilConstants.TypeOfBoolean);

                Type delegateType = Utils.GetFuncDelegateType(typeArguments.ToArray());
                LambdaExpression lambdaOfJoinCondition = Expression.Lambda(delegateType, joinCondition, parameters);

                ret.Add(new Tuple<JoinType, Expression>((JoinType)inputJoinType, lambdaOfJoinCondition));
            }

            return ret;
        }
        static Dictionary<MappingMemberDescriptor, object> CreateKeyValueMap(TypeDescriptor typeDescriptor)
        {
            Dictionary<MappingMemberDescriptor, object> keyValueMap = new Dictionary<MappingMemberDescriptor, object>();
            foreach (MappingMemberDescriptor keyMemberDescriptor in typeDescriptor.PrimaryKeys)
            {
                keyValueMap.Add(keyMemberDescriptor, null);
            }

            return keyValueMap;
        }
        static DbExpression MakeCondition(Dictionary<MappingMemberDescriptor, object> keyValueMap, DbTable dbTable)
        {
            DbExpression conditionExp = null;
            foreach (var kv in keyValueMap)
            {
                MappingMemberDescriptor keyMemberDescriptor = kv.Key;
                object keyVal = kv.Value;

                if (keyVal == null)
                    throw new ArgumentException(string.Format("The primary key '{0}' could not be null.", keyMemberDescriptor.MemberInfo.Name));

                DbExpression left = new DbColumnAccessExpression(dbTable, keyMemberDescriptor.Column);
                DbExpression right = DbExpression.Parameter(keyVal, keyMemberDescriptor.MemberInfoType);
                DbExpression equalExp = new DbEqualExpression(left, right);
                conditionExp = conditionExp == null ? equalExp : DbExpression.And(conditionExp, equalExp);
            }

            return conditionExp;
        }

    }
}