namespace AzureTableAccessor.Data.Impl
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using Azure.Data.Tables;
    using Builders;
    using Data;
    using Infrastructure;
    using Infrastructure.Internal;
    using Mappers;
    using ILocalQueryProvider = AzureTableAccessor.Data.IQueryProvider;

    internal class TableClientRuntimeProxyRepository<TEntity> : IRepository<TEntity> 
        where TEntity : class
    {
        private readonly Type _runtimeType;
        private readonly MethodInfo _createMethod;
        private readonly MethodInfo _updateMethod;
        private readonly MethodInfo _deleteMethod;
        private readonly MethodInfo _loadMethod;
        private readonly MethodInfo _querySingleMethod;
        private readonly MethodInfo _queryAllMethod;
        private readonly MethodInfo _queryMethod;
        private readonly MethodInfo _getPageMethod;
        private readonly IEnumerable<IPropertyRuntimeMapper<TEntity>> _mappers;
        private readonly TableClient _client;
        private readonly Dictionary<string, object> _internalTableCache = new Dictionary<string, object>();
        private const string _cacheKeyPattern = "{0}-{1}";
        private readonly static ConcurrentDictionary<string, Func<object, object[], Task>> _methodsCache
            = new ConcurrentDictionary<string, Func<object, object[], Task>>();

        public TableClientRuntimeProxyRepository(TableServiceClient tableService, Type type,
            IEnumerable<IPropertyRuntimeMapper<TEntity>> mappers, ITableNameProvider tableNameProvider)
        {
            _mappers = mappers;
            _runtimeType = type;
            _client = tableService.GetTableClient(tableNameProvider.GetTableName());

            _createMethod = GetType().FindNonPublicGenericMethod(nameof(CreateAsync));
            _updateMethod = GetType().FindNonPublicGenericMethod(nameof(UpdateAsync));
            _deleteMethod = GetType().FindNonPublicGenericMethod(nameof(DeleteAsync));
            _loadMethod = GetType().FindNonPublicGenericMethod(nameof(LoadAsync));
            _querySingleMethod = GetType().FindNonPublicGenericMethod(nameof(SingleAsync));
            _queryAllMethod = GetType().FindNonPublicGenericMethod(nameof(GetCollectionAsync), 3);
            _queryMethod = GetType().FindNonPublicGenericMethod(nameof(GetCollectionAsync), 4);
            _getPageMethod = GetType().FindNonPublicGenericMethod(nameof(GetPageAsync));
        }

        public Task CreateAsync(TEntity entity, CancellationToken cancellationToken = default)
        {
            var mapper = new ToRuntimeTypeMapper<TEntity>(entity, _mappers);
            var method = CreateExecutedMethod(_createMethod);

            var factory = InstanceFactoryProvider.InstanceFactoryCache.GetOrAdd(_runtimeType,
                (t) => Expression.Lambda<Func<object>>(Expression.New(t)).Compile());

            var instance = factory();

            return method(this, new object[] { mapper, instance, _client, cancellationToken });
        }

        public Task UpdateAsync(TEntity entity, CancellationToken cancellationToken = default)
        {
            var mapper = new ToRuntimeTypeMapper<TEntity>(entity, _mappers);
            var keys = _mappers.GetKeysFromEntity(entity);
            var method = CreateExecutedMethod(_updateMethod);

            return method(this, new object[] { mapper, keys.partitionKey, keys.rowKey, _client, cancellationToken });
        }

        public Task DeleteAsync(TEntity entity, CancellationToken cancellationToken = default)
        {
            var keys = _mappers.GetKeysFromEntity(entity);
            var method = CreateExecutedMethod(_deleteMethod);

            return method(this, new object[] { keys.partitionKey, keys.rowKey, _client, cancellationToken });
        }

        public async Task<TEntity> LoadAsync(TEntity entity, CancellationToken cancellationToken = default)
        {
            var keys = _mappers.GetKeysFromEntity(entity);
            var results = new List<TEntity>();
            var mapper = new FromRuntimeTypeMapper<TEntity>(results, _mappers);
            var method = CreateExecutedMethod(_loadMethod);

            await method(this, new object[] { mapper, keys.partitionKey, keys.rowKey, _client, cancellationToken })
                .ConfigureAwait(false);

            entity = results.FirstOrDefault();
            return entity;
        }

        public async Task<TEntity> SingleAsync(Expression<Func<TEntity, bool>> predicate,
             CancellationToken cancellationToken = default)
        {
            var results = new List<TEntity>();
            var mapper = new FromRuntimeTypeMapper<TEntity>(results, _mappers);
            var query = new RuntimeQueryMapper<TEntity>(predicate, _mappers.Where(e => e is ITranslateVisitorBuilderVisitor)
                .Select(e => e as ITranslateVisitorBuilderVisitor).ToList());
            var method = CreateExecutedMethod(_querySingleMethod);

            await method(this, new object[] { query, mapper, _client, cancellationToken })
                .ConfigureAwait(false);

            return results.FirstOrDefault();
        }

        public async Task<IEnumerable<TEntity>> GetCollectionAsync(Expression<Func<TEntity, bool>> predicate,
             CancellationToken cancellationToken = default)
        {
            var results = new List<TEntity>();
            var mapper = new FromRuntimeTypeMapper<TEntity>(results, _mappers);
            var query = new RuntimeQueryMapper<TEntity>(predicate, _mappers.Where(e => e is ITranslateVisitorBuilderVisitor)
                .Select(e => e as ITranslateVisitorBuilderVisitor).ToList());
            var method = CreateExecutedMethod(_queryMethod);

            await method(this, new object[] { query, mapper, _client, cancellationToken })
                .ConfigureAwait(false);

            return results;
        }

        public async Task<IEnumerable<TEntity>> GetCollectionAsync(CancellationToken cancellationToken = default)
        {
            var results = new List<TEntity>();
            var mapper = new FromRuntimeTypeMapper<TEntity>(results, _mappers);
            var method = CreateExecutedMethod(_queryAllMethod);

            await method(this, new object[] { mapper, _client, cancellationToken })
                .ConfigureAwait(false);

            return results;
        }

        public async Task<Page<TEntity>> GetPageAsync(int pageSize = 100, string continuationToken = null,
             CancellationToken cancellationToken = default)
        {
            var results = new List<TEntity>();
            var mapper = new FromRuntimeTypeMapper<TEntity>(results, _mappers);
            var genericMethod = _getPageMethod.MakeGenericMethod(_runtimeType);
            var tokens = new List<string>();
            var method = CreateExecutedMethod(_getPageMethod);

            await method(this, new object[] { mapper, tokens, continuationToken, pageSize, _client, cancellationToken })
                .ConfigureAwait(false);

            return new Page<TEntity>(results, tokens.FirstOrDefault(), pageSize);
        }

        private Func<object, object[], Task> CreateExecutedMethod(MethodInfo methodInfo)
            => _methodsCache.GetOrAdd($"{methodInfo.Name}-{(_runtimeType.Name)}-{methodInfo.GetParameters().Count()}",
             key =>
            {
                var genericMethod = methodInfo.MakeGenericMethod(_runtimeType);
                return MethodFactory.CreateGenericMethod<Task>(genericMethod);
            });

        #region Runtime methods
        private async Task CreateAsync<T>(IMapper mapper, T entity, TableClient client,
             CancellationToken cancellationToken) where T : class, ITableEntity, new()
        {
            mapper.Map(entity);

            await client.CreateIfNotExistsAsync(cancellationToken)
                .ConfigureAwait(false);

            await client.AddEntityAsync(entity, cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task UpdateAsync<T>(IMapper mapper, string partitionKey, string rowKey, TableClient client,
             CancellationToken cancellationToken) where T : class, ITableEntity, new()
        {
            T entity = null;
            try
            {
                var key = string.Format(_cacheKeyPattern, partitionKey, rowKey);
                if (_internalTableCache.TryGetValue(key, out var cache))
                {
                    entity = (T)cache;
                }
                else
                {
                    var response = await client.GetEntityAsync<T>(partitionKey, rowKey, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);

                    entity = response.Value;
                }
                if (entity != null)
                    mapper.Map(entity);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                throw new EntityNotFoundException(partitionKey, rowKey);
            }

            var updated = await client.UpdateEntityAsync(entity, entity.ETag, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (updated.Headers.ETag.HasValue)
                entity.ETag = updated.Headers.ETag.Value;
        }

        private async Task DeleteAsync<T>(string partitionKey, string rowKey, TableClient client,
             CancellationToken cancellationToken) where T : class, ITableEntity, new()
        {
            T entity = null;
            try
            {
                var key = string.Format(_cacheKeyPattern, partitionKey, rowKey);
                if (_internalTableCache.TryGetValue(key, out var cache))
                {
                    entity = (T)cache;
                }
                else
                {
                    var response = await client.GetEntityAsync<T>(partitionKey, rowKey, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    entity = response.Value;
                }
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                throw new EntityNotFoundException(partitionKey, rowKey);
            }

            await client.DeleteEntityAsync(partitionKey, rowKey, entity.ETag, cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task LoadAsync<T>(IMapper mapper, string partitionKey, string rowKey, TableClient client,
             CancellationToken cancellationToken) where T : class, ITableEntity, new()
        {
            if (string.IsNullOrEmpty(partitionKey)) throw new ArgumentNullException(nameof(partitionKey));
            if (string.IsNullOrEmpty(rowKey)) throw new ArgumentNullException(nameof(rowKey));

            try
            {
                var result = await client.GetEntityAsync<T>(partitionKey, rowKey, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                _internalTableCache[string.Format(_cacheKeyPattern, partitionKey, rowKey)] = result.Value;
                mapper.Map(result.Value);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {

            }
        }

        private async Task GetCollectionAsync<T>(IMapper mapper, TableClient client,
             CancellationToken cancellationToken) where T : class, ITableEntity, new()
        {
            var pages = client.QueryAsync<T>(cancellationToken: cancellationToken).AsPages();

            await foreach (var page in pages.ConfigureAwait(false))
            {
                if (page.Values != null)
                {
                    foreach (var entity in page.Values)
                        mapper.Map(entity);
                }
            }
        }

        private async Task GetPageAsync<T>(IMapper mapper, List<string> tokens, string continuationToken, int pageSize,
             TableClient client, CancellationToken cancellationToken) where T : class, ITableEntity, new()
        {
            var pages = client.QueryAsync<T>(cancellationToken: cancellationToken).AsPages(continuationToken, pageSize);

            var pageEnumerator = pages.GetAsyncEnumerator();
            await pageEnumerator.MoveNextAsync().ConfigureAwait(false);

            var page = pageEnumerator.Current;
            if (page.Values != null)
            {
                foreach (var entity in page.Values)
                    mapper.Map(entity);
            }
            if (page.ContinuationToken != null)
                tokens.Add(page.ContinuationToken);
        }

        private async Task GetCollectionAsync<T>(ILocalQueryProvider query, IMapper mapper, TableClient client,
             CancellationToken cancellationToken) where T : class, ITableEntity, new()
        {
            var pages = client.QueryAsync(query.Query<T>(), cancellationToken: cancellationToken).AsPages();

            await foreach (var page in pages.ConfigureAwait(false))
            {
                if (page.Values != null)
                {
                    foreach (var entity in page.Values)
                        mapper.Map(entity);
                }
            }
        }

        private async Task SingleAsync<T>(ILocalQueryProvider query, IMapper mapper, TableClient client,
             CancellationToken cancellationToken) where T : class, ITableEntity, new()
        {
            var pages = client.QueryAsync(query.Query<T>(), cancellationToken: cancellationToken).AsPages(pageSizeHint: 1);
            var enumerator = pages.GetAsyncEnumerator();
            await enumerator.MoveNextAsync().ConfigureAwait(false);

            var page = enumerator.Current;
            if (page.Values != null)
            {
                foreach (var entity in page.Values)
                    mapper.Map(entity);
            }
        }
        #endregion
    }
}
