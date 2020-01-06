// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

namespace AndreyKurdiumov.AspNetCore.Helpers
{
    using System.Linq;
    using Enumerable = System.Linq.Enumerable;
    using Queryable = System.Linq.Queryable;

    /// <summary>
    ///     Default data source that sorts results if a sort column is specified.
    /// </summary>
    internal sealed class WebGridDataSource : IWebGridDataSource
    {
        private static readonly System.Reflection.MethodInfo SortGenericExpressionMethod = typeof(WebGridDataSource).GetMethod
            (
             "SortGenericExpression",
             System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
            );

        private readonly WebGrid _grid;
        private readonly System.Type _elementType;
        private readonly System.Collections.Generic.IEnumerable<dynamic> _values;
        private readonly bool _canPage;
        private readonly bool _canSort;

        public WebGridDataSource
        (
            WebGrid grid,
            System.Collections.Generic.IEnumerable<dynamic> values,
            System.Type elementType,
            bool canPage,
            bool canSort
        )
        {
            System.Diagnostics.Debug.Assert
                (
                 grid != null
                );
            System.Diagnostics.Debug.Assert
                (
                 values != null
                );

            this._grid = grid;
            this._values = values;
            this._elementType = elementType;
            this._canPage = canPage;
            this._canSort = canSort;
        }

        public SortInfo DefaultSort { get; set; }

        public int RowsPerPage { get; set; }

        public int TotalRowCount
        {
            get
            {
                return Enumerable.Count
                    (
                     this._values
                    );
            }
        }

        public System.Collections.Generic.IList<WebGridRow> GetRows
        (
            SortInfo sortInfo,
            int pageIndex
        )
        {
            var rowData = this._values;

            if(this._canSort)
                rowData = this.Sort
                (
                 rowData.AsQueryable
                     (),
                 sortInfo
                );

            rowData = this.Page
                (
                 rowData,
                 pageIndex
                );

            try
            {
                // Force compile the underlying IQueryable
                rowData = rowData.ToList();
            }
            catch(System.ArgumentException)
            {
                // The OrderBy method uses a generic comparer which fails when the collection contains 2 or more 
                // items that cannot be compared (e.g. DBNulls, mixed types such as strings and ints et al) with the exception
                // System.ArgumentException: At least one object must implement IComparable.
                // Silently fail if this exception occurs and declare that the two items are equivalent
                rowData = this.Page
                    (
                     Queryable.AsQueryable
                         (
                          this._values
                         ),
                     pageIndex
                    );
            }

            return Enumerable.ToList
                (
                 Enumerable.Select
                     (
                      rowData,
                      (
                          value,
                          index
                      )=>new WebGridRow
                          (
                           this._grid,
                           value : value,
                           rowIndex : index
                          )
                     )
                );
        }

        private System.Linq.IQueryable<dynamic> Sort
        (
            System.Linq.IQueryable<dynamic> data,
            SortInfo sortInfo
        )
        {
            if(!string.IsNullOrEmpty
                   (
                    sortInfo.SortColumn
                   )
            || this.DefaultSort != null
            && !string.IsNullOrEmpty
                   (
                    this.DefaultSort.SortColumn
                   ))
            {
                return this.Sort
                    (
                     data,
                     this._elementType,
                     sortInfo
                    );
            }

            return data;
        }

        private System.Collections.Generic.IEnumerable<dynamic> Page
        (
            System.Collections.Generic.IEnumerable<dynamic> data,
            int pageIndex
        )
        {
            if(!this._canPage)
                return data;

            System.Diagnostics.Debug.Assert
                (
                 this.RowsPerPage > 0
                );

            return Enumerable.Take
                (
                 Enumerable.Skip
                     (
                      data,
                      pageIndex * this.RowsPerPage
                     ),
                 this.RowsPerPage
                );
        }

        private System.Linq.IQueryable<dynamic> Sort
        (
            System.Linq.IQueryable<dynamic> data,
            System.Type elementType,
            SortInfo sort
        )
        {
            System.Diagnostics.Debug.Assert
                (
                 data != null
                );

            if(typeof(System.Dynamic.IDynamicMetaObjectProvider).IsAssignableFrom
                (
                 elementType
                ))
            {
                // IDynamicMetaObjectProvider properties are only available through a runtime binder, so we
                // must build a custom LINQ expression for getting the dynamic property value.
                // Lambda: o => o.Property (where Property is obtained by runtime binder)
                // NOTE: lambda must not use internals otherwise this will fail in partial trust when Helpers assembly is in GAC
                var binder = Microsoft.CSharp.RuntimeBinder.Binder.GetMember
                    (
                     Microsoft.CSharp.RuntimeBinder.CSharpBinderFlags.None,
                     sort.SortColumn,
                     typeof(WebGrid),
                     new[]
                     {
                         Microsoft.CSharp.RuntimeBinder.CSharpArgumentInfo.Create
                             (
                              Microsoft.CSharp.RuntimeBinder.CSharpArgumentInfoFlags.None,
                              null
                             )
                     }
                    );
                var param = System.Linq.Expressions.Expression.Parameter
                    (
                     typeof(System.Dynamic.IDynamicMetaObjectProvider),
                     "o"
                    );
                var getter = System.Linq.Expressions.Expression.Dynamic
                    (
                     binder,
                     typeof(object),
                     param
                    );

                return WebGridDataSource.SortGenericExpression<System.Dynamic.IDynamicMetaObjectProvider, object>
                    (
                     data,
                     getter,
                     param,
                     sort.SortDirection
                    );
            }

            System.Linq.Expressions.Expression sorterFunctionBody;
            System.Linq.Expressions.ParameterExpression sorterFunctionParameter;

            System.Linq.Expressions.Expression sorter;

            if(this._grid.CustomSorters.TryGetValue
                (
                 sort.SortColumn,
                 out sorter
                ))
            {
                var lambda = sorter as System.Linq.Expressions.LambdaExpression;
                System.Diagnostics.Debug.Assert
                    (
                     lambda != null
                    );

                sorterFunctionBody = lambda.Body;
                sorterFunctionParameter = lambda.Parameters[0];
            }
            else
            {
                // The IQueryable<dynamic> data source is cast as IQueryable<object> at runtime. We must call
                // SortGenericExpression using reflection so that the LINQ expressions use the actual element type.
                // Lambda: o => o.Property[.NavigationProperty,etc]
                sorterFunctionParameter = System.Linq.Expressions.Expression.Parameter
                    (
                     elementType,
                     "o"
                    );
                System.Linq.Expressions.Expression member = sorterFunctionParameter;
                var type = elementType;
                var sorts = sort.SortColumn.Split
                    (
                     '.'
                    );

                foreach(var name in sorts)
                {
                    var prop = type.GetProperty
                        (
                         name
                        );

                    if(prop == null)
                    {
                        // no-op in case navigation property came from querystring (falls back to default sort)
                        if(this.DefaultSort != null
                        && !sort.Equals
                               (
                                this.DefaultSort
                               )
                        && !string.IsNullOrEmpty
                               (
                                this.DefaultSort.SortColumn
                               ))
                        {
                            return this.Sort
                                (
                                 data,
                                 elementType,
                                 this.DefaultSort
                                );
                        }

                        return data;
                    }

                    member = System.Linq.Expressions.Expression.Property
                        (
                         member,
                         prop
                        );
                    type = prop.PropertyType;
                }

                sorterFunctionBody = member;
            }

            var actualSortMethod = WebGridDataSource.SortGenericExpressionMethod.MakeGenericMethod
                (
                 elementType,
                 sorterFunctionBody.Type
                );

            return (System.Linq.IQueryable<dynamic>)actualSortMethod.Invoke
                (
                 null,
                 new object[]
                 {
                     data,
                     sorterFunctionBody,
                     sorterFunctionParameter,
                     sort.SortDirection
                 }
                );
        }

        private static System.Linq.IQueryable<TElement> SortGenericExpression<TElement, TProperty>
        (
            System.Linq.IQueryable<dynamic> data,
            System.Linq.Expressions.Expression body,
            System.Linq.Expressions.ParameterExpression param,
            SortDirection sortDirection
        )
        {
            System.Diagnostics.Debug.Assert
                (
                 data != null
                );
            System.Diagnostics.Debug.Assert
                (
                 body != null
                );
            System.Diagnostics.Debug.Assert
                (
                 param != null
                );

            // The IQueryable<dynamic> data source is cast as an IQueryable<object> at runtime.  We must cast
            // this to an IQueryable<TElement> so that the reflection done by the LINQ expressions will work.
            var data2 = Queryable.Cast<TElement>
                (
                 data
                );
            var lambda = System.Linq.Expressions.Expression.Lambda<System.Func<TElement, TProperty>>
                (
                 body,
                 param
                );

            if(sortDirection == SortDirection.Descending)
                return Queryable.OrderByDescending
                    (
                     data2,
                     lambda
                    );

            return Queryable.OrderBy
                (
                 data2,
                 lambda
                );
        }
    }
}