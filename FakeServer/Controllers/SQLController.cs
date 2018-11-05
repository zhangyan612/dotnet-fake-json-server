using FakeServer.Common;
using JsonFlatFileDataStore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Threading.Tasks;

namespace FakeServer.Controllers
{
    [Authorize]
    [Route(Config.SQLRoute)]
    public class SQLController : Controller
    {
        private readonly IDataStore _ds;
        private readonly ApiSettings _settings;

        public SQLController(IDataStore ds, IOptions<ApiSettings> settings)
        {
            _ds = ds;
            _settings = settings.Value;
        }

        /// <summary>
        /// List keys
        /// </summary>
        /// <returns>List of keys</returns>
        [HttpGet]
        [HttpHead]
        public IEnumerable<string> GetKeys()
        {
            return _ds.GetKeys().Select(e => e.Key);
        }

		/// <summary>
		/// Select items form table
		/// </summary>
		/// <remarks>
		/// Add filtering with query parameters. E.q. /api/users?age=22&amp;name=Phil (not possible with Swagger)
		///
		/// Optional parameter names skip/take and offset/limit:
		/// /api/users?skip=10&amp;take=20
		/// /api/users?offset=10&amp;limit=20
		/// </remarks>
		/// <param name="name">Name of table</param>
		/// <param name="skip">Items to skip (optional name offset)</param>
		/// <param name="take">Items to take (optional name limit, default to 1000)</param>
		/// <returns>List of items</returns>
		/// <response code="200">Collection item array</response>
		/// <response code="400">Invalid query parameters</response>
		/// <response code="404">Collection not found</response>
		[HttpGet("{collectionId}")]
        [HttpHead("{collectionId}")]
        public IActionResult GetItems(string name, int skip = 0, int take = 1000)
        {
            var found = _ds.GetKeys().TryGetValue(name, out var itemType);

			if (found == false)
                return NotFound();

            if (itemType == JsonFlatFileDataStore.ValueType.Item)
                return GetSingleItem(name);
            else
                return GetCollectionItem(name, skip, take);
        }

        private IActionResult GetSingleItem(string itemId)
        {
            var item = _ds.GetItem(itemId);

            if (item == null)
                return NotFound();

            // TODO: Add results object or is it needed?
            return Ok(item);
        }

        private IActionResult GetCollectionItem(string collectionId, int skip, int take)
        {
            var collection = _ds.GetCollection(collectionId);

            // Collection can actually just be empty, but in this case we handle it as it is not found
            if (!collection.AsQueryable().Any())
                return NotFound();

            var options = QueryHelper.GetQueryOptions(Request.Query, skip, take);

            if (!options.Validate())
                return BadRequest();

            var datas = options.IsTextSearch ? collection.Find(Request.Query["q"]) : collection.AsQueryable();

            foreach (var key in options.QueryParams)
            {
                string propertyName = key;
                Func<dynamic, dynamic, bool> compareFunc = ObjectHelper.Funcs[""];

                var idx = key.LastIndexOf("_");

                if (idx != -1)
                {
                    var op = key.Substring(idx);
                    compareFunc = ObjectHelper.Funcs[op];
                    propertyName = key.Replace(op, "");
                }

                datas = datas.Where(d => ObjectHelper.GetPropertyAndCompare(d as ExpandoObject, propertyName, Request.Query[key], compareFunc));
            }

            var totalCount = datas.Count();

            var paginationHeader = QueryHelper.GetPaginationHeader($"{Request.Scheme}://{Request.Host.Value}{Request.Path}", totalCount, options.Skip, options.Take, options.SkipWord, options.TakeWord);

            var results = datas.Skip(options.Skip).Take(options.Take);

            if (options.Fields.Any())
            {
                results = ObjectHelper.SelectFields(results, options.Fields);
            }

            if (options.SortFields.Any())
            {
                results = SortHelper.SortFields(results, options.SortFields);
            }

            if (_settings.UseResultObject)
            {
                return Ok(QueryHelper.GetResultObject(results, totalCount, paginationHeader, options));
            }
            else
            {
                Response.Headers.Add("X-Total-Count", totalCount.ToString());
                Response.Headers.Add("Link", QueryHelper.GetHeaderLink(paginationHeader));
                return Ok(results);
            }
        }

		//private static List<string> ConstructArgs(NameValueCollection myCol)
		//{
		//	List<string> args = new List<string>();

		//	foreach (string k in myCol.AllKeys) // skip 1 if first one is stored proc name
		//	{
		//		var key = "@" + k;
		//		var value = myCol[k];
		//		args.Add(key);
		//		args.Add(value);
		//	}
		//	return args;
		//}


		/// <summary>
		/// Get single item from table by id
		/// </summary>
		/// <param name="tableName">Stored Procedure Name</param>
		/// <param name="id">Item id</param>
		/// <returns>Item</returns>
		/// <response code="200">Item found</response>
		/// <response code="400">Item is not collection</response>
		/// <response code="404">Item not found</response>
		[HttpGet("{tableName}/{id}")]
        [HttpHead("{tableName}/{id}")]
        public IActionResult GetItem(string tableName, [FromRoute][DynamicBinder]dynamic id)
        {
            if (_ds.IsItem(tableName))
                return BadRequest();

            var result = _ds.GetCollection(tableName).Find(e => e.id == id).FirstOrDefault();

            if (result == null)
                return NotFound();

			return Ok(result);
        }

        
        /// <summary>
        /// Add new item
        /// </summary>
        /// <param name="collectionId">Collection id</param>
        /// <param name="item">Item to add</param>
        /// <returns>Created item id</returns>
        /// <response code="201">Item created</response>
        /// <response code="400">Item is null</response>
        /// <response code="409">Collection is an object</response>
        [HttpPost("{collectionId}")]
        public async Task<IActionResult> AddNewItem(string collectionId, [FromBody]JToken item)
        {
            if (item == null)
                return BadRequest();

            if (_ds.IsItem(collectionId))
                return Conflict();

            var collection = _ds.GetCollection(collectionId);

            await collection.InsertOneAsync(item);

            return Created($"{Request.GetDisplayUrl()}/{item["id"]}", new { id = item["id"] });
        }

        /// <summary>
        /// Replace item from collection
        /// </summary>
        /// <param name="collectionId">Collection id</param>
        /// <param name="id">Id of the item to be replaced</param>
        /// <param name="item">Item's new content</param>
        /// <returns></returns>
        /// <response code="204">Item found and replaced</response>
        /// <response code="400">Replace data is null or item is not in a collection</response>
        /// <response code="404">Item not found</response>
        [HttpPut("{collectionId}/{id}")]
        public async Task<IActionResult> ReplaceItem(string collectionId, [FromRoute][DynamicBinder]dynamic id, [FromBody]dynamic item)
        {
            if (item == null || _ds.IsItem(collectionId))
                return BadRequest();

            // Make sure that new data has id field correctly
            item.id = id;

            var success = await _ds.GetCollection(collectionId).ReplaceOneAsync((Predicate<dynamic>)(e => e.id == id), item, _settings.UpsertOnPut);

            if (success)
                return NoContent();
            else
                return NotFound();
        }

        /// <summary>
        /// Update item's content
        /// </summary>
        /// <remarks>
        /// Patch data contains fields to be updated.
        ///
        ///     {
        ///        "stringField": "some value",
        ///        "intField": 22,
        ///        "boolField": true
        ///     }
        /// </remarks>
        /// <param name="collectionId">Collection id</param>
        /// <param name="id">Id of the item to be updated</param>
        /// <param name="patchData">Patch data</param>
        /// <returns></returns>
        /// <response code="204">Item found and updated</response>
        /// <response code="400">Patch data is empty or item is not in a collection</response>
        /// <response code="404">Item not found</response>
        [HttpPatch("{collectionId}/{id}")]
        public async Task<IActionResult> UpdateItem(string collectionId, [FromRoute][DynamicBinder]dynamic id, [FromBody]JToken patchData)
        {
            dynamic sourceData = JsonConvert.DeserializeObject<ExpandoObject>(patchData.ToString());

            if (!((IDictionary<string, object>)sourceData).Any() || _ds.IsItem(collectionId))
                return BadRequest();

            var success = await _ds.GetCollection(collectionId).UpdateOneAsync((Predicate<dynamic>)(e => e.id == id), sourceData);

            if (success)
                return NoContent();
            else
                return NotFound();
        }

        /// <summary>
        /// Remove item from collection
        /// </summary>
        /// <param name="collectionId">Collection id</param>
        /// <param name="id">Id of the item to be removed</param>
        /// <returns></returns>
        /// <response code="204">Item found and removed</response>
        /// <response code="400">Item is not in a collection</response>
        /// <response code="404">Item not found</response>
        [HttpDelete("{collectionId}/{id}")]
        public async Task<IActionResult> DeleteItem(string collectionId, [FromRoute][DynamicBinder]dynamic id)
        {
            if (_ds.IsItem(collectionId))
                return BadRequest();

            var success = await _ds.GetCollection(collectionId).DeleteOneAsync(e => e.id == id);

            if (success)
                return NoContent();
            else
                return NotFound();
        }

        /// <summary>
        /// Replace object
        /// </summary>
        /// <param name="objectId">Object id</param>
        /// <param name="item">Object's new content</param>
        /// <returns></returns>
        /// <response code="204">Object found and replaced</response>
        /// <response code="400">Replace data is null or item is in a collection</response>
        /// <response code="404">Object not found</response>
        [HttpPut("{objectId}")]
        public async Task<IActionResult> ReplaceSingleItem(string objectId, [FromBody]dynamic item)
        {
            if (_ds.IsCollection(objectId))
                return BadRequest();

            if (item == null)
                return BadRequest();

            var success = await _ds.ReplaceItemAsync(objectId, item, _settings.UpsertOnPut);

            if (success)
                return NoContent();
            else
                return NotFound();
        }

        /// <summary>
        /// Update single object's content
        /// </summary>
        /// <remarks>
        /// Patch data contains fields to be updated.
        ///
        ///     {
        ///        "stringField": "some value",
        ///        "intField": 22,
        ///        "boolField": true
        ///     }
        /// </remarks>
        /// <param name="objectId">Object id</param>
        /// <param name="patchData">Patch data</param>
        /// <returns></returns>
        /// <response code="204">Object found and updated</response>
        /// <response code="400">Patch data is empty</response>
        /// <response code="404">Object not found</response>
        [HttpPatch("{objectId}")]
        public async Task<IActionResult> UpdateSingleItem(string objectId, [FromBody]JToken patchData)
        {
            dynamic sourceData = JsonConvert.DeserializeObject<ExpandoObject>(patchData.ToString());

            if (!((IDictionary<string, object>)sourceData).Any() || _ds.IsCollection(objectId))
                return BadRequest();

            var success = await _ds.UpdateItemAsync(objectId, sourceData);

            if (success)
                return NoContent();
            else
                return NotFound();
        }

        /// <summary>
        /// Remove single object
        /// </summary>
        /// <param name="objectId">Single object id</param>
        /// <returns></returns>
        /// <response code="204">Object found and removed</response>
        /// <response code="400">Object is a collection</response>
        /// <response code="404">Object not found</response>
        [HttpDelete("{objectId}")]
        public async Task<IActionResult> DeleteSingleItem(string objectId)
        {
            if (_ds.IsCollection(objectId))
                return BadRequest();

            var success = await _ds.DeleteItemAsync(objectId);

            if (success)
                return NoContent();
            else
                return NotFound();
        }
    }
}